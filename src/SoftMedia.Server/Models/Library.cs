using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

public enum LibraryType
{
    Movie,
    TV,
    Music,
    Book,
    Game,
    Photo
}

public class Library
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Name { get; set; } = string.Empty;

    public LibraryType Type { get; set; }

    // Stored as JSON
    public List<string> Paths { get; set; } = new();
}
