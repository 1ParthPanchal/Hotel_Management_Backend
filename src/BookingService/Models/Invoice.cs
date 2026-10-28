using System.Text.Json.Serialization;

namespace BookingService.Models;

public enum InvoiceStatus
{
    ManualUnpaid,
    Paid,
    Refunded
}

public class Invoice
{
    public int Id { get; set; }
    public int BookingId { get; set; }
    public decimal Amount { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.ManualUnpaid;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public Booking Booking { get; set; } = null!;
}
