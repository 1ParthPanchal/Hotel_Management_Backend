using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Data;

namespace NotificationService.Controllers;

[ApiController]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly NotificationDbContext _db;

    public NotificationsController(NotificationDbContext db) => _db = db;

    // GET /api/notifications
    // GET /api/notifications?bookingId=5
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? bookingId)
    {
        var query = _db.Notifications.AsQueryable();

        if (bookingId.HasValue)
            query = query.Where(n => n.BookingId == bookingId.Value);

        var notifications = await query.OrderByDescending(n => n.CreatedAt).ToListAsync();
        return Ok(notifications);
    }
}
