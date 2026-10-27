using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using BookingService.Data;
using BookingService.Models;

namespace BookingService.Controllers;

/// <summary>
/// Handles all booking-related API endpoints.
///
/// Key design decisions:
/// - Availability check uses a DB query, NOT Redis cache (cached availability would show stale data)
/// - Price is fetched from CatalogService synchronously (strong consistency on price snapshot)
/// - Booking + Invoice + Outbox are written in ONE database transaction (atomicity)
/// - Cancel is idempotent: cancelling twice returns the same result
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
    // Returns whether the room is available for the given date range.
    // NOT cached — always hits the database for real-time accuracy.
    [HttpGet("availability")]
    public async Task<IActionResult> CheckAvailability(int roomId, DateTime checkIn, DateTime checkOut)
    {
        if (checkIn >= checkOut)
            return BadRequest("Check-in must be before check-out.");

        var hasOverlap = await _db.Bookings.AnyAsync(b =>
            b.RoomId == roomId &&
            b.Status == BookingStatus.Confirmed &&
            b.CheckIn < checkOut &&
            b.CheckOut > checkIn);

        return Ok(new { roomId, checkIn, checkOut, available = !hasOverlap });
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
    // Creates a booking. Steps:
    // 1. Validate dates
    // 2. Check for overlapping confirmed bookings (returns 409 if overlap)
    // 3. Call CatalogService for price snapshot
    // 4. Save Booking + Invoice + OutboxMessage in ONE transaction
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBookingRequest req)
    {
        if (req.CheckIn >= req.CheckOut)
            return BadRequest("Check-in must be before check-out.");

        // Step 2: Check for overlapping bookings
        // Overlap condition: existing.CheckIn < newCheckOut AND existing.CheckOut > newCheckIn
        var overlap = await _db.Bookings.AnyAsync(b =>
            b.RoomId == req.RoomId &&
            b.Status == BookingStatus.Confirmed &&
            b.CheckIn < req.CheckOut &&
            b.CheckOut > req.CheckIn);

        if (overlap)
            return Conflict(new { error = $"Room {req.RoomId} is already booked for the selected dates. Please choose different dates." });

        // Step 3: Get price from CatalogService (synchronous call for price snapshot)
        // WHY SYNC: We need the exact price at booking time. An async event might arrive late.
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

        var nights = (req.CheckOut - req.CheckIn).Days;
        var totalAmount = pricePerNight * nights;

        // Step 4: Atomic transaction — Booking + Invoice + OutboxMessage all together
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var booking = new Booking
            {
                RoomId = req.RoomId,
                GuestName = req.GuestName,
                CheckIn = req.CheckIn,
                CheckOut = req.CheckOut,
                TotalAmount = totalAmount,
                Status = BookingStatus.Confirmed
            };
            _db.Bookings.Add(booking);
            await _db.SaveChangesAsync(); // Get the booking ID

            // Create invoice (local, no payment gateway)
            var invoice = new Invoice
            {
                BookingId = booking.Id,
                Amount = totalAmount,
                Status = InvoiceStatus.ManualUnpaid
            };
            _db.Invoices.Add(invoice);

            // Write outbox message IN THE SAME TRANSACTION
            // The relay will pick this up and publish to RabbitMQ within 5 seconds
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
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to create booking");
            return StatusCode(500, "Failed to create booking.");
        }
    }

    // POST /api/bookings/{id}/cancel
    // Cancels a booking. Idempotent — safe to call multiple times.
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var booking = await _db.Bookings.FindAsync(id);
        if (booking is null) return NotFound();

        // Idempotency: if already cancelled, return the same booking (no error)
        if (booking.Status == BookingStatus.Cancelled)
        {
            _logger.LogInformation("Booking {Id} was already cancelled — returning existing state", id);
            return Ok(booking);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
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

// DTO for deserializing room data from CatalogService
public record RoomDto(int Id, int PropertyId, string Name, decimal PricePerNight, int Capacity);

public record CreateBookingRequest(int RoomId, string GuestName, DateTime CheckIn, DateTime CheckOut);
