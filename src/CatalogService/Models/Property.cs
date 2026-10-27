namespace CatalogService.Models;

public class Property
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public List<Room> Rooms { get; set; } = new();
}
