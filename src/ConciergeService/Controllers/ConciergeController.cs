using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace ConciergeService.Controllers;

[ApiController]
[Route("api/concierge")]
public class ConciergeController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ConciergeController> _logger;

    public ConciergeController(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ConciergeController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] ConciergeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest("A question must be provided.");

        var catalogClient = _httpClientFactory.CreateClient("CatalogService");
        var bookingClient = _httpClientFactory.CreateClient("BookingService");
        var ollamaClient = _httpClientFactory.CreateClient("Ollama");

        // 1. Fetch real catalog data
        string catalogData;
        try
        {
            var catRes = await catalogClient.GetAsync("api/catalog/properties");
            if (!catRes.IsSuccessStatusCode)
                return StatusCode(503, new { error = "CatalogService is currently unavailable." });

            catalogData = await catRes.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach CatalogService");
            return StatusCode(503, new { error = "CatalogService is unreachable." });
        }

        // 2. Fetch real availability if requested for specific room and dates
        string availabilityData = "No specific room availability requested.";
        if (request.RoomId.HasValue && request.CheckIn.HasValue && request.CheckOut.HasValue)
        {
            try
            {
                var availUrl = $"api/bookings/availability?roomId={request.RoomId.Value}&checkIn={request.CheckIn.Value:yyyy-MM-dd}&checkOut={request.CheckOut.Value:yyyy-MM-dd}";
                var availRes = await bookingClient.GetAsync(availUrl);
                if (availRes.IsSuccessStatusCode)
                {
                    availabilityData = await availRes.Content.ReadAsStringAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query BookingService for availability");
            }
        }

        // 3. Grounding validation: Check if user asks about a non-existent room ID
        RoomInfo? matchedRoom = null;
        PropertyInfo? matchedProperty = null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var properties = JsonSerializer.Deserialize<List<PropertyInfo>>(catalogData, options);

            if (properties != null)
            {
                if (request.RoomId.HasValue)
                {
                    foreach (var prop in properties)
                    {
                        var rm = prop.Rooms?.FirstOrDefault(r => r.Id == request.RoomId.Value);
                        if (rm != null)
                        {
                            matchedRoom = rm;
                            matchedProperty = prop;
                            break;
                        }
                    }

                    if (matchedRoom == null)
                    {
                        return Ok(new
                        {
                            answer = $"I am sorry, but Room ID {request.RoomId.Value} does not exist in LocalStay's catalog. We currently offer Deluxe King Suite (ID 1, Riyadh), Executive Double Room (ID 2, Riyadh), and Ocean View Studio (ID 3, Jeddah).",
                            grounded = true,
                            source = "CatalogService Grounding Guard"
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse catalog JSON for pre-validation");
        }

        // 4. Determine Ollama model (default llama3.1:8b, fallback to available pulled model)
        var ollamaModel = _config["Ollama:Model"] ?? "llama3.1:8b";
        try
        {
            var tagsRes = await ollamaClient.GetAsync("api/tags");
            if (tagsRes.IsSuccessStatusCode)
            {
                var tagsJson = await tagsRes.Content.ReadAsStringAsync();
                using var tagsDoc = JsonDocument.Parse(tagsJson);
                var models = tagsDoc.RootElement.GetProperty("models");
                var hasConfiguredModel = false;
                string? firstAvailable = null;

                foreach (var m in models.EnumerateArray())
                {
                    var name = m.GetProperty("name").GetString();
                    if (name != null)
                    {
                        if (name.StartsWith(ollamaModel, StringComparison.OrdinalIgnoreCase))
                            hasConfiguredModel = true;
                        firstAvailable ??= name;
                    }
                }

                if (!hasConfiguredModel && firstAvailable != null)
                {
                    ollamaModel = firstAvailable;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query Ollama tags");
        }

        // 5. Construct grounded system prompt
        var systemPrompt = $@"You are LocalStay's AI Hotel Concierge.
STRICT GROUNDING RULES:
1. ONLY answer using the VERIFIED DATA provided below.
2. NEVER invent, hallucinate, or assume hotel names, locations, room types, prices, or availability.
3. If the user asks about a city, hotel, price, or amenity not present in the VERIFIED DATA, you MUST politely refuse: state that LocalStay does not have that information and direct them to our verified properties in Riyadh and Jeddah.
4. Always state exact prices and capacities as given in the data.

VERIFIED CATALOG PROPERTIES AND ROOMS:
{catalogData}

VERIFIED AVAILABILITY STATUS:
{availabilityData}

GUEST QUESTION:
{request.Question}";

        // 6. Query local Ollama instance (180s timeout)
        var payload = JsonSerializer.Serialize(new
        {
            model = ollamaModel,
            prompt = systemPrompt,
            stream = false
        });

        try
        {
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var ollamaRes = await ollamaClient.PostAsync("api/generate", content);

            if (!ollamaRes.IsSuccessStatusCode)
            {
                return StatusCode(503, new
                {
                    error = $"Ollama returned status code {ollamaRes.StatusCode}.",
                    fix = $"Ensure Ollama is running and model '{ollamaModel}' is pulled via 'ollama pull {ollamaModel}'."
                });
            }

            var resJson = await ollamaRes.Content.ReadAsStringAsync();
            using var resDoc = JsonDocument.Parse(resJson);
            var answer = resDoc.RootElement.GetProperty("response").GetString();

            return Ok(new
            {
                answer,
                model = ollamaModel,
                grounded = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama connection failed");
            return StatusCode(503, new
            {
                error = "Local AI concierge is currently unavailable.",
                fix = $"Ensure Ollama is running on your host machine and model '{ollamaModel}' is pulled. Run: 'ollama run {ollamaModel}' in your terminal."
            });
        }
    }
}

public record ConciergeRequest(string Question, int? RoomId, DateTime? CheckIn, DateTime? CheckOut);
public record PropertyInfo(int Id, string Name, string City, List<RoomInfo>? Rooms);
public record RoomInfo(int Id, int PropertyId, string Name, decimal PricePerNight, int Capacity);
