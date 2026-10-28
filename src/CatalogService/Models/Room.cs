using System.Text.Json.Serialization;

namespace CatalogService.Models;

public class Room
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PricePerNight { get; set; }
    public int Capacity { get; set; }

    [JsonIgnore]
    public Property Property { get; set; } = null!;
}
