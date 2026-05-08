namespace SoftMedia.Server.Services.Security.LibraryAccess;

/// <summary>
/// Resolves the <see cref="LibraryAccess"/> policy for the current request.
/// Caches per-HttpContext so a request that triggers multiple repository
/// reads does not pay the user-row lookup more than once.
/// </summary>
public interface IUserLibraryAccessProvider
{
    Task<LibraryAccess> GetCurrentAsync();
}
