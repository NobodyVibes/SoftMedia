using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

public class Invite
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Code { get; set; } = string.Empty;

    public Guid CreatedById { get; set; }
    public User? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public Guid? UsedById { get; set; }
    public User? UsedBy { get; set; }

    public bool IsRevoked { get; set; } = false;
}
