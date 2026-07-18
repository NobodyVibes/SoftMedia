using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Security.ContentRating;

public class UserContentRatingProvider : IUserContentRatingProvider
{
    // Cache key on HttpContext.Items so a request that triggers multiple repo
    // calls does not pay the user-row lookup cost more than once.
    private const string ItemsKey = "softmedia.userRatingCeilings";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppDbContext _db;

    public UserContentRatingProvider(IHttpContextAccessor httpContextAccessor, AppDbContext db)
    {
        _httpContextAccessor = httpContextAccessor;
        _db = db;
    }

    public async Task<UserRatingCeilings> GetCurrentAsync()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null)
        {
            // No HTTP context = background service / scanner. SDD §6.2 lets
            // those see everything (they do not stream content to users).
            return UserRatingCeilings.Unrestricted;
        }

        if (ctx.Items.TryGetValue(ItemsKey, out var cached) && cached is UserRatingCeilings c)
        {
            return c;
        }

        var resolved = await ResolveAsync(ctx);
        ctx.Items[ItemsKey] = resolved;
        return resolved;
    }

    private async Task<UserRatingCeilings> ResolveAsync(HttpContext ctx)
    {
        var principal = ctx.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return UserRatingCeilings.Unrestricted;
        }

        // Admins bypass parental-control filtering. The role claim is set by
        // TokenService at login from User.Role.
        if (principal.IsInRole(UserRole.Admin.ToString()))
        {
            return UserRatingCeilings.Unrestricted;
        }

        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                 ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var userId))
        {
            // Malformed sub claim. The bearer middleware already validated
            // the token signature, so reaching here means a SoftMedia-issued
            // token has a non-Guid sub — an internal bug rather than an
            // attacker scenario. Fail open so a real user is not locked out
            // by something they cannot fix.
            return UserRatingCeilings.Unrestricted;
        }

        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            return UserRatingCeilings.Unrestricted;
        }

        // Admin bypass must hold even when the role CLAIM is absent. The reduced-privilege
        // media token and the cast token deliberately omit the role claim (TokenService), so
        // the principal.IsInRole check above is false for those tokens. Without this
        // authoritative DB-role bypass, an admin streaming via a media/cast token is subjected
        // to their own MaxRating ceiling (legacy rows may carry "PG-13"; "" = unrestricted since
        // R-WI-011), so any higher-rated title 404s on the stream/tracks endpoints while staying
        // browsable via the full-JWT browse endpoints.
        if (user.Role == UserRole.Admin)
        {
            return UserRatingCeilings.Unrestricted;
        }

        return UserRatingCeilings.From(user);
    }
}
