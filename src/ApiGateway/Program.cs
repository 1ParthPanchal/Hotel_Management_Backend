using Yarp.ReverseProxy;

var builder = WebApplication.CreateBuilder(args);

// ── YARP Reverse Proxy ────────────────────────────────────────────────────────
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseCors();

// ── Unified Swagger UI on Gateway ──────────────────────────────────────────────
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.SwaggerEndpoint("/swagger-docs/catalog/openapi/v1.json", "Catalog Service API");
    options.SwaggerEndpoint("/swagger-docs/booking/openapi/v1.json", "Booking Service API");
    options.SwaggerEndpoint("/swagger-docs/notification/openapi/v1.json", "Notification Service API");
    options.SwaggerEndpoint("/swagger-docs/concierge/openapi/v1.json", "Concierge Service API");
    options.DocumentTitle = "LocalStay Microservices API Gateway";
});

app.MapReverseProxy();

// Basic health endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ApiGateway" }));

// Readiness probe: verifies that Gateway can reach downstream microservices
app.MapGet("/health/ready", async (IHttpClientFactory httpFactory, IConfiguration config) =>
{
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(3);

    var catalogUrl = config["ReverseProxy:Clusters:catalog-cluster:Destinations:destination1:Address"] ?? "http://localhost:5101/";
    var bookingUrl = config["ReverseProxy:Clusters:booking-cluster:Destinations:destination1:Address"] ?? "http://localhost:5102/";
    var notifUrl = config["ReverseProxy:Clusters:notification-cluster:Destinations:destination1:Address"] ?? "http://localhost:5103/";

    var failures = new List<string>();

    try
    {
        var res = await client.GetAsync(new Uri(new Uri(catalogUrl), "health"));
        if (!res.IsSuccessStatusCode) failures.Add($"CatalogService at {catalogUrl} ({res.StatusCode})");
    }
    catch { failures.Add($"CatalogService at {catalogUrl} (unreachable)"); }

    try
    {
        var res = await client.GetAsync(new Uri(new Uri(bookingUrl), "health"));
        if (!res.IsSuccessStatusCode) failures.Add($"BookingService at {bookingUrl} ({res.StatusCode})");
    }
    catch { failures.Add($"BookingService at {bookingUrl} (unreachable)"); }

    try
    {
        var res = await client.GetAsync(new Uri(new Uri(notifUrl), "health"));
        if (!res.IsSuccessStatusCode) failures.Add($"NotificationService at {notifUrl} ({res.StatusCode})");
    }
    catch { failures.Add($"NotificationService at {notifUrl} (unreachable)"); }

    if (failures.Count > 0)
        return Results.Json(new { status = "unhealthy", service = "ApiGateway", failed = failures }, statusCode: 503);

    return Results.Ok(new { status = "healthy", service = "ApiGateway", downstream = "all connected" });
});

app.Run();
