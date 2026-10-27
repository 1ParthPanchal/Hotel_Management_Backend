using Microsoft.EntityFrameworkCore;
using NotificationService.Models;

namespace NotificationService.Data;

/// <summary>
/// EF Core DbContext for the Notification service.
/// The UNIQUE index on (BookingId, Type) is the key idempotency guard:
/// if the same event is published twice (at-least-once delivery), the second
/// INSERT will fail with a unique constraint violation, which we catch and ignore.
/// </summary>
public class NotificationDbContext : DbContext
{
    public NotificationDbContext(DbContextOptions<NotificationDbContext> options) : base(options) { }

    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.Type).HasConversion<string>();
            e.Property(n => n.Message).IsRequired().HasMaxLength(500);

            // UNIQUE constraint: one notification per (BookingId, Type)
            // This is the database-level guard against duplicate event processing.
            e.HasIndex(n => new { n.BookingId, n.Type }).IsUnique();
        });
    }
}
