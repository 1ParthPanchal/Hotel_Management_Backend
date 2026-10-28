using Microsoft.EntityFrameworkCore;
using CatalogService.Data;
using CatalogService.Models;

namespace CatalogService.Data;

/// <summary>
/// Seeds the catalog database with 2 properties and 3 rooms on startup.
/// This runs once when the app starts. In a real project you'd use EF migrations + data seeders.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(CatalogDbContext db)
    {
        // EnsureCreated creates all tables from the model if they don't exist.
        // Week 1 shortcut — real projects use `dotnet ef migrations add` instead.
        await db.Database.MigrateAsync();

        if (db.Properties.Any()) return; // Already seeded

        var prop1 = new Property { Name = "The Grand Palace Hotel", City = "Riyadh" };
        var prop2 = new Property { Name = "Seaside Resort & Spa", City = "Jeddah" };

        db.Properties.AddRange(prop1, prop2);
        await db.SaveChangesAsync();

        db.Rooms.AddRange(
            new Room { PropertyId = prop1.Id, Name = "Deluxe King Suite", PricePerNight = 2500m, Capacity = 2 },
            new Room { PropertyId = prop1.Id, Name = "Executive Double Room", PricePerNight = 4200m, Capacity = 4 },
            new Room { PropertyId = prop2.Id, Name = "Ocean View Studio", PricePerNight = 1800m, Capacity = 2 }
        );
        await db.SaveChangesAsync();
    }
}
