var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

var catalogBaseUrl = builder.Configuration["Services:CatalogService"] ?? "http://localhost:5101/";
var bookingBaseUrl = builder.Configuration["Services:BookingService"] ?? "http://localhost:5102/";
var ollamaBaseUrl = builder.Configuration["Ollama:Host"] ?? "http://localhost:11434/";

builder.Services.AddHttpClient("CatalogService", client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient("BookingService", client =>
{
    client.BaseAddress = new Uri(bookingBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri(ollamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(180); // 180s for cold model start
});

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

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ConciergeService" }));

// Readiness probe
app.MapGet("/health/ready", async (IHttpClientFactory httpFactory, IConfiguration config) =>
{
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(3);

    var ollamaUrl = config["Ollama:Host"] ?? "http://localhost:11434/";
    var catUrl = config["Services:CatalogService"] ?? "http://localhost:5101/";

    var failures = new List<string>();

    try
    {
        var res = await client.GetAsync(new Uri(new Uri(catUrl), "health"));
        if (!res.IsSuccessStatusCode) failures.Add("CatalogService");
    }
    catch { failures.Add("CatalogService (unreachable)"); }

    try
    {
        var res = await client.GetAsync(new Uri(new Uri(ollamaUrl), "api/tags"));
        if (!res.IsSuccessStatusCode) failures.Add("Ollama");
    }
    catch { failures.Add("Ollama (unreachable — start ollama service)"); }

    if (failures.Count > 0)
        return Results.Json(new { status = "unhealthy", service = "ConciergeService", failed = failures }, statusCode: 503);

    return Results.Ok(new { status = "healthy", service = "ConciergeService", ollama = "connected", catalog = "connected" });
});

app.Run();
