using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;
using BookingService.Data;
using BookingService.Models;

namespace BookingService.Controllers;

/// <summary>
/// Core booking workflows:
/// - Availability check (date-range overlap logic)
/// - Create booking (atomic with invoice + transactional outbox)
/// - Cancel booking (status update + outbox event)
///
/// CONCURRENCY GUARANTEE (k6 load tested):
/// - Fetches price snapshot from Catalog BEFORE any DB lock.
/// - Serializable transaction with pg_advisory_xact_lock per room.
/// - Re-checks overlap INSIDE the exclusive advisory lock boundary.
/// - Booking + Invoice + OutboxMessage are written in ONE atomic database transaction.
/// </summary>
[ApiController]
[Route("api/bookings")]
public class BookingsController : ControllerBase
{
    private readonly BookingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BookingsController> _logger;

    public BookingsController(BookingDbContext db, IHttpClientFactory httpClientFactory, ILogger<BookingsController> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // GET /api/bookings/availability?roomId=1&checkIn=2025-01-01&checkOut=2025-01-05
    [HttpGet("availability")]
    public async Task<IActionResult> CheckAvailability(int roomId, DateTime checkIn, DateTime checkOut)
    {
        var checkInUtc = DateTime.SpecifyKind(checkIn, DateTimeKind.Utc);
        var checkOutUtc = DateTime.SpecifyKind(checkOut, DateTimeKind.Utc);

        if (checkInUtc >= checkOutUtc)
            return BadRequest("Check-in must be before check-out.");

        var hasOverlap = await _db.Bookings.AnyAsync(b =>
            b.RoomId == roomId &&
            b.Status == BookingStatus.Confirmed &&
            b.CheckIn < checkOutUtc &&
            b.CheckOut > checkInUtc);

        return Ok(new { roomId, checkIn = checkInUtc, checkOut = checkOutUtc, available = !hasOverlap });
    }

    // GET /api/bookings
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var bookings = await _db.Bookings.Include(b => b.Invoice).OrderByDescending(b => b.CreatedAt).ToListAsync();
        return Ok(bookings);
    }

    // GET /api/bookings/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var booking = await _db.Bookings.Include(b => b.Invoice).FirstOrDefaultAsync(b => b.Id == id);
        return booking is null ? NotFound() : Ok(booking);
    }

    // POST /api/bookings
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBookingRequest req)
    {
        var checkInUtc = DateTime.SpecifyKind(req.CheckIn, DateTimeKind.Utc);
        var checkOutUtc = DateTime.SpecifyKind(req.CheckOut, DateTimeKind.Utc);

        if (checkInUtc >= checkOutUtc)
            return BadRequest("Check-in must be before check-out.");

        // Step 1: Fetch price from CatalogService BEFORE any DB lock
        decimal pricePerNight;
        try
        {
            var client = _httpClientFactory.CreateClient("CatalogService");
            var response = await client.GetAsync($"api/catalog/rooms/{req.RoomId}");
            if (!response.IsSuccessStatusCode)
                return BadRequest($"Room {req.RoomId} not found in Catalog.");

            var roomJson = await response.Content.ReadAsStringAsync();
            var room = JsonSerializer.Deserialize<RoomDto>(roomJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            pricePerNight = room?.PricePerNight ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch room price from CatalogService");
            return StatusCode(503, "Catalog service is unavailable. Please try again.");
        }

        var nights = (checkOutUtc - checkInUtc).Days;
        var totalAmount = pricePerNight * nights;

        // Step 2: Serializable transaction with pg_advisory_xact_lock per room
        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            // Acquire advisory transaction-scoped lock on RoomId
            long lockKey = req.RoomId;
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0});", lockKey);

            // Re-check overlap INSIDE the lock
            var overlap = await _db.Bookings.AnyAsync(b =>
                b.RoomId == req.RoomId &&
                b.Status == BookingStatus.Confirmed &&
                b.CheckIn < checkOutUtc &&
                b.CheckOut > checkInUtc);

            if (overlap)
            {
                await transaction.RollbackAsync();
                return Conflict(new { error = $"Room {req.RoomId} is already booked for the selected dates. Please choose different dates." });
            }

            var booking = new Booking
            {
                RoomId = req.RoomId,
                GuestName = req.GuestName,
                CheckIn = checkInUtc,
                CheckOut = checkOutUtc,
                TotalAmount = totalAmount,
                Status = BookingStatus.Confirmed
            };
            _db.Bookings.Add(booking);
            await _db.SaveChangesAsync();

            var invoice = new Invoice
            {
                BookingId = booking.Id,
                Amount = totalAmount,
                Status = InvoiceStatus.ManualUnpaid
            };
            _db.Invoices.Add(invoice);

            var payload = JsonSerializer.Serialize(new
            {
                EventId = Guid.NewGuid(),
                booking.Id,
                booking.RoomId,
                booking.GuestName,
                booking.CheckIn,
                booking.CheckOut,
                booking.TotalAmount,
                OccurredAt = DateTime.UtcNow
            });
            _db.OutboxMessages.Add(new OutboxMessage { EventType = "booking.created", Payload = payload });

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation("Booking {Id} created for {Guest}", booking.Id, booking.GuestName);
            return CreatedAtAction(nameof(GetById), new { id = booking.Id }, booking);
        }
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            await transaction.RollbackAsync();
            _logger.LogWarning("Concurrent booking conflict (40001) for room {RoomId} on dates {CheckIn} to {CheckOut}", req.RoomId, checkInUtc, checkOutUtc);
            return Conflict(new { error = $"Room {req.RoomId} is already booked for the selected dates. Please choose different dates." });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to create booking");
            return StatusCode(500, "Failed to create booking.");
        }
    }

    private static bool IsSerializationFailure(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is PostgresException pex && (pex.SqlState == "40001" || pex.SqlState == "40P01"))
                return true;
            ex = ex.InnerException;
        }
        return false;
    }

    // POST /api/bookings/{id}/cancel
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var booking = await _db.Bookings.FindAsync(id);
        if (booking is null) return NotFound();

        if (booking.Status == BookingStatus.Cancelled)
            return Ok(booking);

        await using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            booking.Status = BookingStatus.Cancelled;

            var payload = JsonSerializer.Serialize(new
            {
                EventId = Guid.NewGuid(),
                booking.Id,
                booking.RoomId,
                booking.GuestName,
                booking.CheckIn,
                booking.CheckOut,
                booking.TotalAmount,
                OccurredAt = DateTime.UtcNow
            });
            _db.OutboxMessages.Add(new OutboxMessage { EventType = "booking.cancelled", Payload = payload });

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(booking);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to cancel booking {Id}", id);
            return StatusCode(500, "Failed to cancel booking.");
        }
    }
}

public record RoomDto(int Id, int PropertyId, string Name, decimal PricePerNight, int Capacity);
public record CreateBookingRequest(int RoomId, string GuestName, DateTime CheckIn, DateTime CheckOut);
