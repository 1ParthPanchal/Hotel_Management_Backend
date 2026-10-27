using Microsoft.EntityFrameworkCore;
using BookingService.Models;

namespace BookingService.Data;

/// <summary>
/// EF Core DbContext for the Booking service.
/// Contains: Bookings, Invoices, and the Outbox (transactional outbox pattern).
/// All three tables live in booking_db.
/// </summary>
public class BookingDbContext : DbContext
{
    public BookingDbContext(DbContextOptions<BookingDbContext> options) : base(options) { }

    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Booking>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.GuestName).IsRequired().HasMaxLength(200);
            e.Property(b => b.TotalAmount).HasColumnType("decimal(10,2)");
            e.Property(b => b.Status).HasConversion<string>();
            e.HasOne(b => b.Invoice).WithOne(i => i.Booking).HasForeignKey<Invoice>(i => i.BookingId);
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Amount).HasColumnType("decimal(10,2)");
            e.Property(i => i.Status).HasConversion<string>();
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.EventType).IsRequired().HasMaxLength(100);
            e.Property(o => o.Payload).IsRequired();
        });
    }
}
