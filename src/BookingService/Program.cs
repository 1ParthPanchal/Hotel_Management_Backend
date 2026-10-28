using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;
using BookingService.Data;
using BookingService.Messaging;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

var connectionString = builder.Configuration.GetConnectionString("BookingDb")
    ?? "Host=localhost;Port=5433;Database=booking_db;Username=localstay;Password=localstay123";

builder.Services.AddDbContext<BookingDbContext>(opt =>
{
    opt.UseNpgsql(connectionString);
    opt.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// HttpClient for calling CatalogService to fetch room price at booking time (idempotent GET ONLY)
// Polly retry policy: exponential backoff 2s / 4s / 8s on transient HTTP errors (5xx, 408)
// NEVER attach retry to POST bookings or non-idempotent operations!
static IAsyncPolicy<HttpResponseMessage> GetCatalogRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) // 2s, 4s, 8s
        );
}

var catalogBaseUrl = builder.Configuration["Services:CatalogService"]
    ?? "http://localhost:5101/";

builder.Services.AddHttpClient("CatalogService", client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
})
.AddPolicyHandler(GetCatalogRetryPolicy());

// Outbox relay — publishes messages to RabbitMQ every 5 seconds
builder.Services.AddHostedService<OutboxRelayService>();

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

// Basic health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "BookingService" }));

// Readiness probe: verifies PostgreSQL connection
app.MapGet("/health/ready", async (BookingDbContext db) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        if (!canConnect)
            return Results.Json(new { status = "unhealthy", service = "BookingService", failed = "PostgreSQL (booking_db)" }, statusCode: 503);

        return Results.Ok(new { status = "healthy", service = "BookingService", database = "connected" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", service = "BookingService", failed = "PostgreSQL (booking_db)", error = ex.Message }, statusCode: 503);
    }
});

// ── Startup ────────────────────────────────────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
