namespace BookingService.Models;

public enum BookingStatus
{
    Confirmed,
    Cancelled
}

/// <summary>
/// A booking made by a guest for a specific room.
/// Overlap check: CheckIn &lt; other.CheckOut AND other.CheckIn &lt; CheckOut
/// </summary>
public class Booking
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public decimal TotalAmount { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Confirmed;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Invoice? Invoice { get; set; }
}
