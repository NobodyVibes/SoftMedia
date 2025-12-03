using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

public record LoginRequest(string Username, string Password);

public record SignupRequest(string Username, string Password, string? InviteCode, string FirstName, string LastName);

public record AuthResponse(string AccessToken, UserDto User);

public record UserDto(Guid Id, string Username, UserRole Role, string MaxRating, DateTime CreatedAt, bool IsBanned, bool IsApproved, bool IsRejected, Dictionary<string, string> ContentRatings, string FirstName, string LastName, bool CreatedByAdmin, string? UsedInviteCode);

// User Management DTOs
public record UpdateUserRoleRequest(string Role);

public record ApproveUserRequest(bool IsApproved);

public record BanUserRequest(bool IsBanned);

public record CreateUserRequest(string Username, string Password, string Role, string FirstName, string LastName);

public record UpdateUserRatingsRequest(Dictionary<string, string> ContentRatings);

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

