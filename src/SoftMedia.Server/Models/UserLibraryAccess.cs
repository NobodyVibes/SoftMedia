using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// Per-user allow-list for libraries. Presence semantics:
///   - Zero rows for a user => unrestricted (sees every library).
///   - At least one row    => allow-list (sees only those libraries).
/// Admins always bypass this filter regardless of rows.
/// </summary>
[PrimaryKey(nameof(UserId), nameof(LibraryId))]
public class UserLibraryAccess
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid LibraryId { get; set; }
    public Library Library { get; set; } = null!;
}
