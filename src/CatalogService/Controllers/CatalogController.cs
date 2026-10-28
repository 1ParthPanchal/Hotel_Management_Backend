using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using CatalogService.Data;
using CatalogService.Models;

namespace CatalogService.Controllers;

/// <summary>
/// Handles all catalog-related API endpoints.
/// Redis cache-aside pattern: try cache first, fallback to DB, store in cache for 5 minutes.
/// Availability is NOT cached here — that lives in BookingService.
/// </summary>
[ApiController]
[Route("api/catalog")]
public class CatalogController : ControllerBase
{
    private readonly CatalogDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly ILogger<CatalogController> _logger;
    private const int CacheTtlMinutes = 5;

    public CatalogController(CatalogDbContext db, IDistributedCache cache, ILogger<CatalogController> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    // GET /api/catalog/properties
    // Returns all properties. Cached in Redis for 5 minutes.
    [HttpGet("properties")]
    public async Task<IActionResult> GetProperties()
    {
        const string cacheKey = "catalog:properties";
        var cached = await _cache.GetStringAsync(cacheKey);

        if (cached is not null)
        {
            _logger.LogInformation("Cache HIT for {Key}", cacheKey);
            Response.Headers["X-Cache"] = "HIT";
            return Content(cached, "application/json");
        }

        _logger.LogInformation("Cache MISS for {Key} — loading from DB", cacheKey);
        Response.Headers["X-Cache"] = "MISS";
        var properties = await _db.Properties.Include(p => p.Rooms).ToListAsync();
        var json = JsonSerializer.Serialize(properties);

        await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheTtlMinutes)
        });

        return Ok(properties);
    }

    // GET /api/catalog/properties/{id}/rooms
    // Returns rooms for a specific property. Cached per property.
    [HttpGet("properties/{id}/rooms")]
    public async Task<IActionResult> GetRooms(int id)
    {
        var cacheKey = $"catalog:property:{id}:rooms";
        var cached = await _cache.GetStringAsync(cacheKey);

        if (cached is not null)
        {
            _logger.LogInformation("Cache HIT for {Key}", cacheKey);
            Response.Headers["X-Cache"] = "HIT";
            return Content(cached, "application/json");
        }

        _logger.LogInformation("Cache MISS for {Key} — loading from DB", cacheKey);
        Response.Headers["X-Cache"] = "MISS";
        var rooms = await _db.Rooms.Where(r => r.PropertyId == id).ToListAsync();
        if (!rooms.Any()) return NotFound($"No rooms found for property {id}");

        var json = JsonSerializer.Serialize(rooms);
        await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheTtlMinutes)
        });

        return Ok(rooms);
    }

    // GET /api/catalog/rooms/{id}
    // Used by BookingService to get the price snapshot synchronously when creating a booking.
    [HttpGet("rooms/{id}")]
    public async Task<IActionResult> GetRoom(int id)
    {
        var cacheKey = $"catalog:room:{id}";
        var cached = await _cache.GetStringAsync(cacheKey);

        if (cached is not null)
        {
            _logger.LogInformation("Cache HIT for {Key}", cacheKey);
            Response.Headers["X-Cache"] = "HIT";
            return Content(cached, "application/json");
        }

        _logger.LogInformation("Cache MISS for {Key} — loading from DB", cacheKey);
        Response.Headers["X-Cache"] = "MISS";
        var room = await _db.Rooms.FindAsync(id);
        if (room is null) return NotFound();

        var json = JsonSerializer.Serialize(room);
        await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(CacheTtlMinutes)
        });

        return Ok(room);
    }

    // POST /api/catalog/properties — for testing/admin
    [HttpPost("properties")]
    public async Task<IActionResult> CreateProperty([FromBody] Property property)
    {
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        // Invalidate the list cache so the next GET fetches fresh data
        await _cache.RemoveAsync("catalog:properties");
        return CreatedAtAction(nameof(GetProperties), new { id = property.Id }, property);
    }

    // POST /api/catalog/properties/{id}/rooms — for testing/admin
    [HttpPost("properties/{id}/rooms")]
    public async Task<IActionResult> CreateRoom(int id, [FromBody] Room room)
    {
        room.PropertyId = id;
        _db.Rooms.Add(room);
        await _db.SaveChangesAsync();
        await _cache.RemoveAsync($"catalog:property:{id}:rooms");
        await _cache.RemoveAsync("catalog:properties");
        return CreatedAtAction(nameof(GetRooms), new { id }, room);
    }
}
