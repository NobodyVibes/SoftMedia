using System.Security.Claims;
using SoftMedia.Server.Services.Identity;

namespace SoftMedia.Server.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var idClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user ID");
        }
        return userId;
    }

    /// <summary>
    /// True when this principal's access token was issued to a user who must change
    /// their password before using the API (security audit C1). Set via the
    /// <see cref="AuthClaims.MustChangePassword"/> claim in <see cref="TokenService"/>.
    /// </summary>
    public static bool MustChangePassword(this ClaimsPrincipal user)
        => user.HasClaim(AuthClaims.MustChangePassword, "true");
}
