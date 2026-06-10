namespace SoftMedia.Server.Services.Identity;

/// <summary>
/// Minimum password policy (security audit L2). Centralised so signup, change-password, and
/// admin reset all enforce the same floor instead of accepting empty/1-character passwords.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 256; // guard against absurd inputs (Argon2 hashes any length)

    /// <summary>Returns null when the password is acceptable, or a human-readable reason if not.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return "Password is required.";
        if (password.Length < MinLength)
            return $"Password must be at least {MinLength} characters.";
        if (password.Length > MaxLength)
            return $"Password must be at most {MaxLength} characters.";
        return null;
    }
}
