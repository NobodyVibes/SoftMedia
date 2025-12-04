using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

public enum UserRole
{
    User,
    Admin
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public Guid? ParentId { get; set; }

    public string MaxRating { get; set; } = "PG-13";
    
    // JSON string storing ratings per type: { "Movie": "PG-13", "TV": "TV-14", "Game": "T" }
    public string ContentRatings { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsBanned { get; set; } = false;

    public bool IsApproved { get; set; } = false;
    public bool IsRejected { get; set; } = false;

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public bool CreatedByAdmin { get; set; } = false;

    public string? RefreshToken { get; set; }

    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    public bool MustChangePassword { get; set; } = false;
}
