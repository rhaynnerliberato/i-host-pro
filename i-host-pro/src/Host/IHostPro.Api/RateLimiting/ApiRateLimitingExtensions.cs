using System.Threading.RateLimiting;
using IHostPro.BuildingBlocks.Infrastructure.RateLimiting;
using IHostPro.Contexts.Identity.Api.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace IHostPro.Api.RateLimiting;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting), Decision Gate §2 —
/// the four approved HTTP categories, wired onto ASP.NET Core's own
/// <c>Microsoft.AspNetCore.RateLimiting</c> middleware (native technology
/// preferred over a bespoke pipeline, per §29 of the CP0 preflight for this
/// checkpoint's own mandate) via <see cref="DistributedRateLimiterAdapter"/>.
///
/// Partition keys never use PII or a user-entered secret:
/// <list type="bullet">
/// <item><b>Authentication</b>: the real TCP peer IP — the exact same
/// <c>HttpContext.Connection.RemoteIpAddress</c> technique <c>AuthController</c>
/// already uses for audit (never <c>X-Forwarded-For</c> — no trusted-proxy
/// configuration exists in this host). Applied to Login/Refresh only.</item>
/// <item><b>TenantApi</b>: the authenticated TenantId claim — the default
/// for every other controller-routed endpoint (<c>MapControllers().RequireRateLimiting(...)</c>
/// below).</item>
/// <item><b>AdminApi</b>: TenantId+UserId — both internal identifiers, never
/// external PII — applied only to the small set of administrative
/// controllers (user/role management) via <c>[EnableRateLimiting("AdminApi")]</c>
/// on the controller, which overrides the broader default above for exactly
/// those endpoints.</item>
/// </list>
///
/// Webhook (Meta WhatsApp) is deliberately NOT wired here — its correct
/// partition key (a provider/account-level technical identifier, never the
/// guest's phone) only becomes known after the controller itself reads and
/// verifies the raw request body, so it calls <see cref="IDistributedRateLimiter"/>
/// directly instead (see <c>WhatsAppWebhookController</c>).
/// </summary>
public static class ApiRateLimitingExtensions
{
    public const string AuthenticationPolicy = "Authentication";
    public const string TenantApiPolicy = "TenantApi";
    public const string AdminApiPolicy = "AdminApi";

    /// <summary>
    /// Fase 12, Checkpoint 3 — applies <paramref name="defaultPolicyName"/>
    /// ONLY to endpoints that don't already declare their own
    /// <see cref="EnableRateLimitingAttribute"/> (Login/Refresh's own
    /// "Authentication" attribute, the administrative controllers' own
    /// "AdminApi" attribute). A plain <c>MapControllers().RequireRateLimiting(...)</c>
    /// was tried first and empirically confirmed WRONG for this purpose — an
    /// endpoint-group convention added via <c>RequireRateLimiting</c> is
    /// composed AFTER the controller/action's own attribute metadata and so
    /// silently overrides it (confirmed via a real E2E test: with a plain
    /// <c>RequireRateLimiting("TenantApi")</c> in place, Login's own
    /// <c>[EnableRateLimiting("Authentication")]</c> never triggered — every
    /// request used TenantApi's much higher limit instead). This convention
    /// checks each endpoint's own metadata first, so it can never re-open
    /// that same bug.
    /// </summary>
    public static void RequireRateLimitingByDefault(this IEndpointConventionBuilder builder, string defaultPolicyName) =>
        builder.Add(endpointBuilder =>
        {
            if (!endpointBuilder.Metadata.OfType<EnableRateLimitingAttribute>().Any())
                endpointBuilder.Metadata.Add(new EnableRateLimitingAttribute(defaultPolicyName));
        });

    public static IServiceCollection AddIHostProHttpRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

                // Never expose which policy/partition/window was hit — a
                // generic body only (mandate §25/§31: no Redis/limiter
                // internals in the response).
                context.HttpContext.Response.ContentType = "text/plain";
                await context.HttpContext.Response.WriteAsync("Too many requests.", cancellationToken);
            };

            options.AddPolicy(AuthenticationPolicy, httpContext =>
            {
                var limiter = httpContext.RequestServices.GetRequiredService<IDistributedRateLimiter>();
                var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.Get(partitionKey, key => new DistributedRateLimiterAdapter(limiter, AuthenticationPolicy, key));
            });

            options.AddPolicy(TenantApiPolicy, httpContext =>
            {
                var limiter = httpContext.RequestServices.GetRequiredService<IDistributedRateLimiter>();
                var partitionKey = AuthenticatedIdentityReader.TryRead(httpContext.User, out var identity)
                    ? identity.TenantId.ToString("N")
                    : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unauthenticated";
                return RateLimitPartition.Get(partitionKey, key => new DistributedRateLimiterAdapter(limiter, TenantApiPolicy, key));
            });

            options.AddPolicy(AdminApiPolicy, httpContext =>
            {
                var limiter = httpContext.RequestServices.GetRequiredService<IDistributedRateLimiter>();
                var partitionKey = AuthenticatedIdentityReader.TryRead(httpContext.User, out var identity)
                    ? $"{identity.TenantId:N}:{identity.UserId:N}"
                    : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unauthenticated";
                return RateLimitPartition.Get(partitionKey, key => new DistributedRateLimiterAdapter(limiter, AdminApiPolicy, key));
            });
        });

        return services;
    }
}
