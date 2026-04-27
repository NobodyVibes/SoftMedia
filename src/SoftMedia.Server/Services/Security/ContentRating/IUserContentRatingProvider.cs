namespace SoftMedia.Server.Services.Security.ContentRating;

/// Resolves the parental-control ceilings in effect for the current request.
///
/// Returns <see cref="UserRatingCeilings.Unrestricted"/> when:
///   - There is no HTTP context (background services, scanners),
///   - The principal is unauthenticated,
///   - The principal is in the Admin role,
///   - The user row cannot be located,
///   - The userId claim is malformed.
///
/// In all "unrestricted" cases the repository filter is a no-op, which is the
/// correct behaviour: scanners and admins must see everything, and unauth'd
/// callers are already rejected upstream by `[Authorize]`.
public interface IUserContentRatingProvider
{
    Task<UserRatingCeilings> GetCurrentAsync();
}
