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
}
