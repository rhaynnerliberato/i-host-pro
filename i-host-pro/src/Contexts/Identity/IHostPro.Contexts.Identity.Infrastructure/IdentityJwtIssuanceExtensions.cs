using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure;

/// <summary>
/// Registers JWT access-token issuance — the RSA signing key
/// (<see cref="JwtSigningKeyOptions"/>/<see cref="IJwtSigningKeyProvider"/>)
/// and <see cref="IJwtTokenGenerator"/> — deliberately kept out of
/// <see cref="IdentityModuleExtensions.AddIdentityModule"/> (Incremento 2
/// plan, Etapa 6). IHostPro.Worker never issues or validates a JWT and must
/// never be required to hold the signing private key or its configuration —
/// call this method ONLY from IHostPro.Api's composition root, never from
/// IHostPro.Worker's.
///
/// <see cref="JwtOptions"/> (issuer/audience/lifetime/clock skew — not
/// sensitive) remains registered by <c>AddIdentityModule</c> for both hosts,
/// unchanged: only the private key material is host-restricted.
/// </summary>
public static class IdentityJwtIssuanceExtensions
{
    public static IServiceCollection AddIdentityJwtIssuance(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtSigningKeyOptions>()
            .Bind(configuration.GetSection(JwtSigningKeyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtSigningKeyOptions>, JwtSigningKeyOptionsValidator>();

        services.AddSingleton<IJwtSigningKeyProvider, ConfigurationJwtSigningKeyProvider>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();

        return services;
    }
}
