namespace SoftMedia.Server.Extensions;

public static class SecurityHeadersExtensions
{
    /// <summary>
    /// Adds baseline security response headers to every response. Registered early in the
    /// pipeline so it covers the API, the static SPA, and error responses.
    ///   - <c>Referrer-Policy: no-referrer</c> — stops an access/streaming token that rides in
    ///     a media URL (<c>?token=</c> / <c>?access_token=</c>) from leaking to third parties via
    ///     the <c>Referer</c> header on outbound navigations (security audit H3).
    ///   - <c>X-Content-Type-Options: nosniff</c> — block MIME sniffing (audit L7).
    ///   - <c>X-Frame-Options: SAMEORIGIN</c> — clickjacking protection. SAMEORIGIN (not DENY)
    ///     so the SPA's own same-origin reader iframes (epub.js) keep working (audit L7).
    /// WS-9 extends this with HSTS and a Content-Security-Policy.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["Referrer-Policy"] = "no-referrer";
            // HSTS (audit L8): tell HTTPS clients to stick to HTTPS. Only emitted on a secure
            // request (Request.IsHttps honours X-Forwarded-Proto from a trusted proxy), so an
            // HTTP-only LAN deployment is unaffected. Browsers ignore HSTS over plain HTTP anyway.
            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }
            await next();
        });
    }
}
