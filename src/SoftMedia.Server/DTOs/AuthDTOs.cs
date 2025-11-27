using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

public record LoginRequest(string Username, string Password);

public record SignupRequest(string Username, string Password, string? InviteCode);

public record AuthResponse(string AccessToken, UserDto User);

public record UserDto(Guid Id, string Username, UserRole Role, string MaxRating);
