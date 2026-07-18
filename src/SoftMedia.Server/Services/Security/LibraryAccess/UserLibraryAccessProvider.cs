using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Security.LibraryAccess;

/// <summary>
/// Mirrors <c>UserContentRatingProvider</c> structurally — same per-request
/// HttpContext caching, same admin-bypass rule, same fail-open-on-malformed-claim
/// posture. The only differences are the lookup table (<c>UserLibraryAccess</c>)
/// and the resolved value type (<see cref="LibraryAccess"/>).
/// </summary>
public class UserLibraryAccessProvider : IUserLibraryAccessProvider
{
    private const string ItemsKey = "softmedia.userLibraryAccess";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppDbContext _db;

    public UserLibraryAccessProvider(IHttpContextAccessor httpContextAccessor, AppDbContext db)
    {
        _httpContextAccessor = httpContextAccessor;
        _db = db;
    }

    public async Task<LibraryAccess> GetCurrentAsync()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null)
        {
            // No HTTP context = background scanner / hosted service. SDD §6.2
            // lets those see everything (they do not stream content to users).
            return LibraryAccess.Unrestricted;
        }

        if (ctx.Items.TryGetValue(ItemsKey, out var cached) && cached is LibraryAccess c)
        {
            return c;
        }

        var resolved = await ResolveAsync(ctx);
        ctx.Items[ItemsKey] = resolved;
        return resolved;
    }

    private async Task<LibraryAccess> ResolveAsync(HttpContext ctx)
    {
        var principal = ctx.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return LibraryAccess.Unrestricted;
        }

        // Admins always bypass. The role claim is set by TokenService at login.
        if (principal.IsInRole(UserRole.Admin.ToString()))
        {
            return LibraryAccess.Unrestricted;
        }

        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                 ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var userId))
        {
            // Bearer middleware already validated signature; reaching here means
            // a SoftMedia-issued token has a non-Guid sub — internal bug, not
            // an attacker. Fail open so a real user is not locked out.
            return LibraryAccess.Unrestricted;
        }

        // Admin bypass must hold even when the role CLAIM is absent. The reduced-privilege
        // media token and the cast token deliberately omit the role claim (TokenService), so
        // the principal.IsInRole check above is false for those tokens. Resolve the role from
        // the DB so an admin streaming via a media/cast token is never wrongly ACL-restricted
        // (mirrors UserContentRatingProvider's authoritative admin bypass).
        var role = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserRole?)u.Role)
            .FirstOrDefaultAsync();
        if (role == UserRole.Admin)
        {
            return LibraryAccess.Unrestricted;
        }

        var allowed = await _db.UserLibraryAccess
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.LibraryId)
            .ToListAsync();

        // Zero rows = unrestricted (the default for every existing user).
        return allowed.Count == 0
            ? LibraryAccess.Unrestricted
            : LibraryAccess.AllowOnly(allowed);
    }
}
