using Yarp.ReverseProxy;

var builder = WebApplication.CreateBuilder(args);

// ── YARP Reverse Proxy ────────────────────────────────────────────────────────
// YARP reads routes and clusters from appsettings.json (or environment variables).
// In Docker, we override the cluster addresses via env vars so services talk by container name.
// On your Windows host, the defaults in appsettings.json point to localhost ports.

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();
app.MapReverseProxy();

// Health endpoint for Docker healthcheck and verification
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ApiGateway" }));

app.Run();
