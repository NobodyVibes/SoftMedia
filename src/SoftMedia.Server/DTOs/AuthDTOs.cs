using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

// NR-WI-005: TokenDelivery selects how the refresh token is returned. Omitted/"cookie"
// (browsers) keeps the HttpOnly-cookie flow; "body" (native/headless clients that have
// no cookie jar) returns the refresh token in the AuthResponse and sets no cookie.
public record LoginRequest(string Username, string Password, string? TokenDelivery = null);

public record SignupRequest(string Username, string Password, string? InviteCode, string FirstName, string LastName);

// RefreshToken is only populated for TokenDelivery="body" flows; null is omitted from
// the wire so the browser-facing shape is unchanged.
public record AuthResponse(string AccessToken, UserDto User,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RefreshToken = null);

// NR-WI-005: body-based refresh/logout for clients without a cookie jar.
public record RefreshRequest(string? RefreshToken);

// NR-WI-006 — Quick Connect device pairing.
public record QuickConnectInitiateRequest(string? DeviceName);
public record QuickConnectInitiateResponse(string Code, string Secret, int ExpiresInSeconds, int PollIntervalSeconds);
public record QuickConnectStateResponse(string Status,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? AccessToken = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RefreshToken = null);
public record QuickConnectPendingResponse(string Code, string? DeviceName, string? RequestIp, DateTime CreatedAt);
public record QuickConnectAuthorizeRequest(string Code);
// The poll secret travels in a POST body, never the query string — query strings land
// in request logs (the WS-6 tokens-out-of-URLs principle applies to pairing secrets too).
public record QuickConnectStateRequest(string? Secret);

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

public record TwoFactorRequest(string ChallengeId, string Code, string? TokenDelivery = null);

// TOTP enrollment DTOs (P2-WI-005).
public record TotpEnrollResponse(string Secret, string OtpAuthUri);
public record TotpConfirmRequest(string Code);
public record TotpConfirmResponse(List<string> RecoveryCodes);
public record TotpStatusResponse(bool Enabled);
public record TotpDisableRequest(string Password, string Code);

// QS-WI-002 appended the remote bitrate + resolution limits (0 = unlimited/inherit, like the base cap).
public record UserDto(Guid Id, string Username, UserRole Role, string MaxRating, DateTime CreatedAt, bool IsBanned, bool IsApproved, bool IsRejected, Dictionary<string, string> ContentRatings, string FirstName, string LastName, bool CreatedByAdmin, string? UsedInviteCode, bool MustChangePassword, bool TwoFactorEnabled, int MaxStreamBitrateKbps, int RemoteMaxStreamBitrateKbps = 0, int MaxStreamResolution = 0);

// User Management DTOs
public record UpdateUserRoleRequest(string Role);

// R-WI-009: admin-only per-user streaming bitrate cap (kbps; 0 = unlimited). Enforced since
// P1-WI-003 but previously settable only by direct DB edit.
// QS-WI-002: remote bitrate variant (applies only off-LAN; beats the base cap there) and a
// resolution ceiling (height in pixels). 0/null = unlimited/inherit. Override-wins semantics:
// a set value replaces the server's network caps for this account and may exceed them.
public record UpdateUserStreamingRequest(int MaxStreamBitrateKbps, int? RemoteMaxStreamBitrateKbps = null, int? MaxStreamResolution = null);

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

