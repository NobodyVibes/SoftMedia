using Microsoft.IdentityModel.Tokens;
using SoftMedia.Server.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace SoftMedia.Server.Services.Identity;

/// <summary>
/// Claim names that mark and scope a Chromecast token. A cast token is a normal user JWT
/// with these two extra claims; <c>JwtBearerEvents.OnTokenValidated</c> enforces that a token
/// carrying <see cref="CastUse"/> is accepted ONLY on the stream/transcode routes for the
/// <see cref="CastMedia"/> media id — never anywhere else, regardless of the user's role.
/// </summary>
public static class CastTokenClaims
{
    public const string TokenUse = "token_use";
    public const string CastUse = "cast";
    public const string CastMedia = "cast_media";
}

public interface ITokenService
{
    string GenerateAccessToken(User user);

    /// <summary>
    /// Issues a long-lived, single-media token for casting. The Chromecast receiver fetches
    /// the stream itself and cannot refresh a short-lived session JWT, so this token outlives a
    /// movie — but is hard-scoped (via <see cref="CastTokenClaims"/>) to one media item's
    /// stream routes so a leaked cast URL cannot act as the user elsewhere.
    /// </summary>
    string GenerateCastToken(User user, Guid mediaId);
}

public class TokenService : ITokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GenerateAccessToken(User user)
    {
        var expiryMinutes = int.Parse(_configuration["JwtSettings:ExpiryMinutes"] ?? "15");
        return BuildToken(IdentityClaims(user), DateTime.UtcNow.AddMinutes(expiryMinutes));
    }

    public string GenerateCastToken(User user, Guid mediaId)
    {
        var expiryHours = int.Parse(_configuration["JwtSettings:CastTokenExpiryHours"] ?? "12");
        // Deliberately OMIT the role claim. A cast token only needs identity (sub) + content
        // rating to stream; leaving out the role means that even if the path-scope check in
        // OnTokenValidated ever regressed, a cast token could never act as an admin.
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim("MaxRating", user.MaxRating),
            // Mark + scope this as a cast token. Enforced in JwtBearerEvents.OnTokenValidated.
            new Claim(CastTokenClaims.TokenUse, CastTokenClaims.CastUse),
            new Claim(CastTokenClaims.CastMedia, mediaId.ToString()),
        };
        return BuildToken(claims, DateTime.UtcNow.AddHours(expiryHours));
    }

    private static List<Claim> IdentityClaims(User user) => new()
    {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        // `jti` gives each token a unique identifier even when two are issued for the same
        // user within the same second — required so rotated tokens are string-distinguishable,
        // and useful for future per-token revocation / audit.
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
        new Claim(ClaimTypes.Role, user.Role.ToString()),
        new Claim("MaxRating", user.MaxRating),
    };

    private string BuildToken(IEnumerable<Claim> claims, DateTime expires)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var secretKey = jwtSettings["Secret"] ?? throw new InvalidOperationException("JWT Secret is missing");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: expires,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
