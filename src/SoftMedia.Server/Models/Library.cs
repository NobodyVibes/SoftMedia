using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

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

[Index(nameof(Order))]
public class Library
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Name { get; set; } = string.Empty;

    public LibraryType Type { get; set; }

    // Stored as JSON
    public List<string> Paths { get; set; } = new();

    public int Order { get; set; }
}
