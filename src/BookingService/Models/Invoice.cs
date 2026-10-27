namespace BookingService.Models;

public enum InvoiceStatus
{
    /// <summary>
    /// ManualUnpaid is the local replacement for a payment gateway.
    /// In a real system you'd integrate Stripe/Moyasar here.
    /// </summary>
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

    public Booking Booking { get; set; } = null!;
}
