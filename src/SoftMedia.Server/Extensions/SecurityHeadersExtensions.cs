namespace SoftMedia.Server.Extensions;

public static class SecurityHeadersExtensions
{
    /// <summary>
    /// Content-Security-Policy for the SPA (audit wave-2 WS-13). Tuned for SoftMedia's actual
    /// sources: same-origin everything, plus <c>blob:</c> for hls.js media workers / react-pdf
    /// (pdf.js) workers, <c>data:</c>/<c>blob:</c> images and media, and <c>'unsafe-inline'</c>
    /// styles (framer-motion injects inline styles; Tailwind ships external CSS). Scripts are
    /// same-origin only — the Vite production build emits external JS, no inline script. Same-origin
    /// iframes (<c>frame-src 'self'</c>) keep the epub.js reader working.
    ///
    /// Shipped in REPORT-ONLY by default so it can never white-screen the SPA: browsers evaluate
    /// it and report violations but enforce nothing. After an operator confirms a clean run
    /// (reader / player / casting / SignalR) they flip <c>Security:EnforceCsp=true</c> to enforce.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "media-src 'self' blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss:; " +
        "worker-src 'self' blob:; " +
        "frame-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'self'";

    /// <summary>
    /// Adds baseline security response headers to every response. Registered early in the
    /// pipeline so it covers the API, the static SPA, and error responses.
    ///   - <c>Referrer-Policy: no-referrer</c> — stops an access/streaming token that rides in
    ///     a media URL (<c>?token=</c> / <c>?access_token=</c>) from leaking to third parties via
    ///     the <c>Referer</c> header on outbound navigations (security audit H3).
    ///   - <c>X-Content-Type-Options: nosniff</c> — block MIME sniffing (audit L7).
    ///   - <c>X-Frame-Options: SAMEORIGIN</c> — clickjacking protection. SAMEORIGIN (not DENY)
    ///     so the SPA's own same-origin reader iframes (epub.js) keep working (audit L7).
    ///   - <c>Content-Security-Policy[-Report-Only]</c> — XSS/clickjacking defence (audit WS-13).
    /// </summary>
    /// <param name="enforceCsp">
    /// When true, emit an ENFORCING <c>Content-Security-Policy</c>; otherwise (default) emit
    /// <c>Content-Security-Policy-Report-Only</c> so the policy is observed but never blocks.
    /// Driven by the <c>Security:EnforceCsp</c> config flag in Program.cs.
    /// </param>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool enforceCsp = false)
    {
        var cspHeaderName = enforceCsp ? "Content-Security-Policy" : "Content-Security-Policy-Report-Only";
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["Referrer-Policy"] = "no-referrer";
            headers[cspHeaderName] = ContentSecurityPolicy;
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
