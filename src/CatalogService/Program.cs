using Microsoft.EntityFrameworkCore;
using CatalogService.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

// PostgreSQL via EF Core. Connection string is supplied by appsettings / environment.
var connectionString = builder.Configuration.GetConnectionString("CatalogDb")
    ?? "Host=localhost;Port=5432;Database=catalog_db;Username=localstay;Password=localstay123";

builder.Services.AddDbContext<CatalogDbContext>(opt =>
    opt.UseNpgsql(connectionString));

// Redis distributed cache for cache-aside pattern (5-min TTL on catalog data)
var redisConnection = builder.Configuration["Redis:Connection"] ?? "localhost:6379";
builder.Services.AddStackExchangeRedisCache(opt => opt.Configuration = redisConnection);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// CORS: Allow the Angular frontend (port 4200) and the API Gateway (port 5000)
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

// Health endpoint — the Gateway and docker healthcheck use this
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "CatalogService" }));

// ── Startup: seed database ─────────────────────────────────────────────────────

// EnsureCreated + seed runs on startup.
// NOTE: Real projects use `dotnet ef migrations add InitialCreate` here.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await DbSeeder.SeedAsync(db);
}

app.Run();
