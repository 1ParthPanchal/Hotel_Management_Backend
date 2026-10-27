using Microsoft.EntityFrameworkCore;
using CatalogService.Models;

namespace CatalogService.Data;

/// <summary>
/// EF Core DbContext for the Catalog service.
/// Contains Properties and Rooms tables in the catalog_db database.
/// </summary>
public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Room> Rooms => Set<Room>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Property>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired().HasMaxLength(200);
            e.Property(p => p.City).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<Room>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).IsRequired().HasMaxLength(200);
            e.Property(r => r.PricePerNight).HasColumnType("decimal(10,2)");
            e.HasOne(r => r.Property)
             .WithMany(p => p.Rooms)
             .HasForeignKey(r => r.PropertyId);
        });
    }
}
