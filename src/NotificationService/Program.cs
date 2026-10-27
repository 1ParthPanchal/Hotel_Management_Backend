using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using NotificationService.Messaging;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

var connectionString = builder.Configuration.GetConnectionString("NotificationDb")
    ?? "Host=localhost;Port=5432;Database=notification_db;Username=localstay;Password=localstay123";

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

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "NotificationService" }));

// ── Startup ────────────────────────────────────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.Run();
