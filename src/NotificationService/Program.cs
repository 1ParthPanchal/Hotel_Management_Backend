using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Messaging;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

var connectionString = builder.Configuration.GetConnectionString("NotificationDb")
    ?? "Host=localhost;Port=5433;Database=notification_db;Username=localstay;Password=localstay123";

builder.Services.AddDbContext<NotificationDbContext>(opt =>
    opt.UseNpgsql(connectionString));

// RabbitMQ consumer — receives booking events and stores notifications
builder.Services.AddHostedService<BookingEventConsumer>();

builder.Services.AddControllers();
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
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "NotificationService" }));

// Readiness probe: verifies PostgreSQL
app.MapGet("/health/ready", async (NotificationDbContext db) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        if (!canConnect)
            return Results.Json(new { status = "unhealthy", service = "NotificationService", failed = "PostgreSQL (notification_db)" }, statusCode: 503);

        return Results.Ok(new { status = "healthy", service = "NotificationService", database = "connected" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", service = "NotificationService", failed = "PostgreSQL (notification_db)", error = ex.Message }, statusCode: 503);
    }
});

// ── Startup ────────────────────────────────────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
