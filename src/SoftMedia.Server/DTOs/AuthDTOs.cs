using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

public record LoginRequest(string Username, string Password);

public record SignupRequest(string Username, string Password, string? InviteCode, string FirstName, string LastName);

public record AuthResponse(string AccessToken, UserDto User);

public record ChangePasswordRequest(string OldPassword, string NewPassword);

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

public record UserDto(Guid Id, string Username, UserRole Role, string MaxRating, DateTime CreatedAt, bool IsBanned, bool IsApproved, bool IsRejected, Dictionary<string, string> ContentRatings, string FirstName, string LastName, bool CreatedByAdmin, string? UsedInviteCode, bool MustChangePassword);

// User Management DTOs
public record UpdateUserRoleRequest(string Role);

public record ApproveUserRequest(bool IsApproved);

public record BanUserRequest(bool IsBanned);

public record CreateUserRequest(string Username, string Password, string Role, string FirstName, string LastName);

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

