using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace SoftMedia.Server.Models.Options;

public class JwtOptions
{
    public const string SectionName = "JwtSettings";

    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? Secret { get; set; }
    public string? ExpiryMinutes { get; set; }

    public static readonly IReadOnlyList<string> BlockedSecrets = new[]
    {
        "ThisIsASecretKeyForSoftMediaDevelopmentOnly_ChangeInProduction"
    };

    public const int MinimumSecretByteLength = 32;
}

public record JwtOptionsValidationResult(bool IsValid, string? ErrorMessage)
{
    public static JwtOptionsValidationResult Valid() => new(true, null);
    public static JwtOptionsValidationResult Invalid(string message) => new(false, message);
}

public static class JwtOptionsValidator
{
    private const string HowToFix =
        "Generate one with `dotnet run -- --generate-jwt-secret` and save it via " +
        "`dotnet user-secrets set \"JwtSettings:Secret\" \"<value>\"` for development, or set the " +
        "`JwtSettings__Secret` environment variable in production. " +
        "See docs/user-guide/configuration.md.";

    public static JwtOptionsValidationResult Validate(IConfiguration configuration)
    {
        var section = configuration.GetSection(JwtOptions.SectionName);
        var secret = section["Secret"];
        return ValidateSecret(secret);
    }

    public static JwtOptionsValidationResult ValidateSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return JwtOptionsValidationResult.Invalid(
                $"JwtSettings:Secret is not configured. {HowToFix}");
        }

        foreach (var blocked in JwtOptions.BlockedSecrets)
        {
            if (string.Equals(secret, blocked, StringComparison.Ordinal))
            {
                return JwtOptionsValidationResult.Invalid(
                    "JwtSettings:Secret is set to the committed placeholder value, which is publicly " +
                    $"known and unsafe to use. {HowToFix}");
            }
        }

        var byteLength = System.Text.Encoding.UTF8.GetByteCount(secret);
        if (byteLength < JwtOptions.MinimumSecretByteLength)
        {
            return JwtOptionsValidationResult.Invalid(
                $"JwtSettings:Secret is too short ({byteLength} UTF-8 bytes); " +
                $"minimum is {JwtOptions.MinimumSecretByteLength} bytes (HMAC-SHA256 key length). {HowToFix}");
        }

        return JwtOptionsValidationResult.Valid();
    }
}

public static class JwtSecretGenerator
{
    public const int SecretByteLength = 64;

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretByteLength);
        return Base64UrlEncoder.Encode(bytes);
    }
}
