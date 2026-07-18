using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

public record LoginRequest(string Username, string Password);

public record SignupRequest(string Username, string Password, string? InviteCode, string FirstName, string LastName);

public record AuthResponse(string AccessToken, UserDto User);

// Returned by /auth/signup when a self-registered account still needs admin approval.
// Carries no token (audit M1) — the client shows a "pending approval" message.
public record SignupPendingResponse(string Status, string Message);

public record ChangePasswordRequest(string OldPassword, string NewPassword);

// Reduced-privilege token for media URLs that ride in the query string (audit H3).
public record MediaTokenResponse(string Token, int ExpiresInMinutes);

// P2-WI-005 — when a user has TOTP enabled, login returns this instead of tokens.
// The client then POSTs the code + challengeId to /auth/2fa to complete login.
public record TwoFactorRequiredResponse(string Status, string ChallengeId)
{
    public TwoFactorRequiredResponse(string challengeId) : this("2fa_required", challengeId) { }
}

public record TwoFactorRequest(string ChallengeId, string Code);

// TOTP enrollment DTOs (P2-WI-005).
public record TotpEnrollResponse(string Secret, string OtpAuthUri);
public record TotpConfirmRequest(string Code);
public record TotpConfirmResponse(List<string> RecoveryCodes);
public record TotpStatusResponse(bool Enabled);
public record TotpDisableRequest(string Password, string Code);

public record UserDto(Guid Id, string Username, UserRole Role, string MaxRating, DateTime CreatedAt, bool IsBanned, bool IsApproved, bool IsRejected, Dictionary<string, string> ContentRatings, string FirstName, string LastName, bool CreatedByAdmin, string? UsedInviteCode, bool MustChangePassword, bool TwoFactorEnabled, int MaxStreamBitrateKbps);

// User Management DTOs
public record UpdateUserRoleRequest(string Role);

// R-WI-009: admin-only per-user streaming bitrate cap (kbps; 0 = unlimited). Enforced since
// P1-WI-003 but previously settable only by direct DB edit.
public record UpdateUserStreamingRequest(int MaxStreamBitrateKbps);

public record ApproveUserRequest(bool IsApproved);

public record BanUserRequest(bool IsBanned);

// R-WI-011: ContentRatings lets the admin set the ceiling AT creation (visible in the modal,
// default = no restrictions per the maintainer decision). Optional for API back-compat.
public record CreateUserRequest(string Username, string Password, string Role, string FirstName, string LastName,
    Dictionary<string, string>? ContentRatings = null);

public record UpdateUserRatingsRequest(Dictionary<string, string> ContentRatings);

public record ResetUserPasswordRequest(string NewPassword);

// Wave C — per-user library ACL. An empty list means "unrestricted" (default).
public class SetLibraryAccessRequest
{
    public List<Guid>? LibraryIds { get; set; }
}

// Invite DTOs
public record InviteDto(
    string Code,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? UsedAt,
    string? UsedByUsername,
    bool IsRevoked
);

public record CreateInviteRequest(int? ExpiresInHours);

