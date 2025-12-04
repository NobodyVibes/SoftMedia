using System.ComponentModel.DataAnnotations;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

public class CreateLibraryRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public LibraryType Type { get; set; }

    [Required]
    public List<string> Paths { get; set; } = new();
}
