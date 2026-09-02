namespace IHostPro.Api.Security;

/// <summary>
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — the standard,
/// low-risk response headers that were entirely absent before this
/// checkpoint (confirmed by direct audit: no <c>X-Content-Type-Options</c>,
/// frame protection, <c>Referrer-Policy</c>, or <c>Permissions-Policy</c>
/// anywhere). Deliberately does NOT set a Content-Security-Policy — this Api
/// serves only JSON to a separately-hosted Angular frontend, and a CSP
/// authored without full knowledge of the frontend's own asset/script
/// origins risks breaking it; that decision is left to whoever configures
/// the frontend's own hosting layer (mandate explicit instruction: never add
/// an arbitrary CSP here).
/// </summary>
public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseIHostProSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            // This Api is never framed (no HTML it serves), so DENY is safe —
            // never SAMEORIGIN/ALLOW-FROM, which would only make sense for an
            // Api that itself serves embeddable content.
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            // A pure JSON Api needs none of these browser features — deny
            // every one of them explicitly rather than leaving the header
            // absent (absence lets the browser default apply, which varies).
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), payment=()";

            await next(context);
        });
}
