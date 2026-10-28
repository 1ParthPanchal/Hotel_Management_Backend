using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using CatalogService.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

var connectionString = builder.Configuration.GetConnectionString("CatalogDb")
    ?? "Host=localhost;Port=5433;Database=catalog_db;Username=localstay;Password=localstay123";

builder.Services.AddDbContext<CatalogDbContext>(opt =>
{
    opt.UseNpgsql(connectionString);
    opt.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// Redis distributed cache for cache-aside pattern (5-min TTL on catalog data)
var redisConnection = builder.Configuration["Redis:Connection"] ?? "localhost:6379";
builder.Services.AddStackExchangeRedisCache(opt => opt.Configuration = redisConnection);

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);

builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// ── Middleware ─────────────────────────────────────────────────────────────────

app.UseCors();
app.MapOpenApi();
app.MapControllers();

// Basic health endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "CatalogService" }));

// Readiness probe: verifies PostgreSQL AND Redis
app.MapGet("/health/ready", async (CatalogDbContext db, IDistributedCache cache) =>
{
    try
    {
        var dbOk = await db.Database.CanConnectAsync();
        if (!dbOk)
            return Results.Json(new { status = "unhealthy", service = "CatalogService", failed = "PostgreSQL (catalog_db)" }, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", service = "CatalogService", failed = "PostgreSQL (catalog_db)", error = ex.Message }, statusCode: 503);
    }

    try
    {
        await cache.SetStringAsync("health:probe", "ok", new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10) });
        var val = await cache.GetStringAsync("health:probe");
        if (val != "ok")
            return Results.Json(new { status = "unhealthy", service = "CatalogService", failed = "Redis" }, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", service = "CatalogService", failed = "Redis", error = ex.Message }, statusCode: 503);
    }

    return Results.Ok(new { status = "healthy", service = "CatalogService", database = "connected", redis = "connected" });
});

// ── Startup: seed database ─────────────────────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await DbSeeder.SeedAsync(db);
}

app.Run();
