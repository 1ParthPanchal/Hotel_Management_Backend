namespace NotificationService.Models;

public enum NotificationType
{
    BookingCreated,
    BookingCancelled
}

/// <summary>
/// A notification stored when a booking event is received from RabbitMQ.
/// The UNIQUE index on (BookingId, Type) prevents duplicate notifications
/// even if the same event is delivered more than once (idempotent consumer).
/// </summary>
public class Notification
{
    public int Id { get; set; }
    public int BookingId { get; set; }
    public NotificationType Type { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
