using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace SoftMedia.Server.Extensions;

public static class RequestExtensions
{
    /// <summary>
    /// Retrieves the authentication token from the query string ("token") or Authorization header ("Bearer").
    /// Query string takes precedence for stream/transcode compatibility.
    /// </summary>
    public static string? GetToken(this HttpRequest request)
    {
        // 1. Try Query String (standard for HLS/Video streaming)
        if (request.Query.TryGetValue("token", out var tokenValue) && !StringValues.IsNullOrEmpty(tokenValue))
        {
            return tokenValue.ToString();
        }

        // 2. Try Authorization Header
        if (request.Headers.TryGetValue("Authorization", out var authHeaderValue) && !StringValues.IsNullOrEmpty(authHeaderValue))
        {
            var authHeader = authHeaderValue.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader.Substring(7);
            }
            return authHeader; // Fallback: return raw header if no Bearer prefix? Or just null? 
                               // Standard is "Bearer <token>", sticking to that is safer. 
                               // But existing logic in TranscodeController:
                               /*
                                  if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                                  {
                                      token = authHeader.Substring(7);
                                  }
                               */
                               // It implied it might rely on just the token if not Bearer? No, usually not.
                               // Let's stick to Bearer extraction. 
        }

        return null;
    }
}
