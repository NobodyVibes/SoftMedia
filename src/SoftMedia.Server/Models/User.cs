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

    // R-WI-011 (maintainer decision 2026-07-17): new users are NEVER content-rating restricted by
    // default — the admin sets ceilings per user explicitly. "" = unrestricted (legacy Movie
    // fallback in UserRatingCeilings.From). The old silent "PG-13" default made higher-rated
    // titles 404 with no explanation. Kept in sync with ContentRatings["Movie"] on every write
    // via UsersController.ApplyContentRatings.
    public string MaxRating { get; set; } = "";

    // JSON string storing ratings per type: { "Movie": "PG-13", "TV": "TV-14", "Game": "T" }
    public string ContentRatings { get; set; } = "{}";

    // R-WI-013 follow-up (maintainer decision 2026-07-17): USER-owned history-privacy toggle.
    // false = this user's plays are never written to PlaybackHistory and never bump the
    // aggregate PlayCount — invisible everywhere, full stop (no "anonymous logging" middle
    // mode: in a small household it de-anonymizes trivially, and the play-dedup mechanism
    // needs the user key to work). Resume positions / watched flags (UserMediaInteraction)
    // are unaffected — this only stops the diary. Default true; existing rows are defaulted
    // true by the AddRecordPlaybackHistoryFlag migration.
    public bool RecordPlaybackHistory { get; set; } = true;

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

    /// <summary>
    /// Per-user streaming bitrate ceiling in kbps. When non-null, overrides the
    /// network-based (LAN/WAN) cap for this user. Null = inherit the server policy.
    /// Admin-set. (P1-WI-003)
    /// </summary>
    public int? MaxStreamBitrateKbps { get; set; }

    /// <summary>
    /// Per-user REMOTE (off-LAN) streaming bitrate ceiling in kbps. When non-null and the
    /// client is off-LAN it takes precedence over <see cref="MaxStreamBitrateKbps"/> and the
    /// WAN cap. Override-wins like the base cap: it may exceed the server's network tier —
    /// that is the feature ("this user's personal limit"). Null = inherit. (QS-WI-002)
    /// </summary>
    public int? RemoteMaxStreamBitrateKbps { get; set; }

    /// <summary>
    /// Per-user streaming resolution ceiling as a height in pixels (720/1080/2160...).
    /// When non-null it REPLACES the server's network resolution cap (RemoteMaxResolution)
    /// for this account — override-wins, so it may exceed that cap. The server-wide
    /// MaxTranscodeResolution hardware guardrail still applies on top. Null = inherit. (QS-WI-002)
    /// </summary>
    public int? MaxStreamResolution { get; set; }
}
