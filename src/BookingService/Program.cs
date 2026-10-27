using Microsoft.EntityFrameworkCore;
using BookingService.Data;
using BookingService.Messaging;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

var connectionString = builder.Configuration.GetConnectionString("BookingDb")
    ?? "Host=localhost;Port=5432;Database=booking_db;Username=localstay;Password=localstay123";

builder.Services.AddDbContext<BookingDbContext>(opt =>
    opt.UseNpgsql(connectionString));

// HttpClient for calling CatalogService to fetch room price at booking time
var catalogBaseUrl = builder.Configuration["Services:CatalogService"]
    ?? "http://localhost:5101/";
builder.Services.AddHttpClient("CatalogService", client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Outbox relay — publishes messages to RabbitMQ every 5 seconds
builder.Services.AddHostedService<OutboxRelayService>();

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

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "BookingService" }));

// ── Startup ────────────────────────────────────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
    // EnsureCreated = Week 1 shortcut. Real projects use EF migrations.
    await db.Database.EnsureCreatedAsync();
}

app.Run();
