using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Authentication;

/// <summary>
/// Registers JWT Bearer authentication/authorization (Incremento 2 plan,
/// Etapa 13; ADR-012) — kept out of <c>AddIdentityModule</c> and
/// <c>AddIdentityJwtIssuance</c>, called ONLY from <c>IHostPro.Api</c>'s
/// composition root, never from <c>IHostPro.Worker</c>'s: the Worker never
/// authenticates an inbound HTTP request and must never carry the signing
/// key, Redis connection, or this middleware (same reasoning as
/// <c>AddIdentityJwtIssuance</c>/<c>AddIdentitySessionRevocationCache</c>).
/// Call this AFTER both of those — <see cref="ConfigureJwtBearerOptions"/>
/// depends on <c>IJwtSigningKeyProvider</c> (from <c>AddIdentityJwtIssuance</c>)
/// and, per-request, on <c>ISessionRevocationCache</c> (from
/// <c>AddIdentitySessionRevocationCache</c> or its no-op default).
/// </summary>
public static class IdentityJwtBearerAuthenticationExtensions
{
    public static IServiceCollection AddIdentityJwtBearerAuthentication(this IServiceCollection services)
    {
        // IConfigureNamedOptions<JwtBearerOptions>, not an inline
        // AddJwtBearer(options => ...) lambda: its dependencies
        // (IJwtSigningKeyProvider, IOptions<JwtOptions>) are supplied by the
        // DI container when the Options infrastructure builds this instance
        // — never by calling BuildServiceProvider() here.
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddAuthorization();

        return services;
    }
}
