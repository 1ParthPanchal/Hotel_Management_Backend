namespace BookingService.Models;

/// <summary>
/// Outbox message written in the SAME transaction as the booking.
/// A background relay reads this table every 5 seconds and publishes to RabbitMQ.
/// This guarantees at-least-once delivery even if the process crashes after SaveChanges
/// but before the publish.
/// </summary>
public class OutboxMessage
{
    public int Id { get; set; }

    /// <summary>booking.created or booking.cancelled</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>JSON payload: EventId, BookingId, RoomId, GuestName, CheckIn, CheckOut, TotalAmount, OccurredAt</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null until successfully published to RabbitMQ</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Number of publish attempts (for dead-letter / alerting)</summary>
    public int Attempts { get; set; } = 0;
}
