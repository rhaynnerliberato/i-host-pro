using IHostPro.Contexts.ExternalIntegrations.Application;
using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// Development-only <see cref="IWhatsAppCredentialProvider"/> — resolves a
/// secret reference from <see cref="IConfiguration"/> (User Secrets/
/// environment variables in Development, never a committed
/// <c>appsettings.json</c> value), mirroring <c>IJwtSigningKeyProvider</c>'s
/// own Development implementation precedent (ADR-012). Registered only when
/// <c>IsDevelopment()</c> (see <c>ExternalIntegrationsModuleExtensions</c>)
/// — never registered for any other environment, so resolving
/// <see cref="IWhatsAppCredentialProvider"/> outside Development fails loudly
/// (no implementation registered) instead of silently falling back to this
/// one (CP2.1 mandate §12/§39: Production must never be faked by falling
/// back to Development configuration).
///
/// Never logs the resolved value.
/// </summary>
public sealed class DevelopmentWhatsAppCredentialProvider : IWhatsAppCredentialProvider
{
    private const string ConfigurationSectionPath = "ExternalIntegrations:WhatsApp:Secrets";

    private readonly IConfiguration _configuration;

    public DevelopmentWhatsAppCredentialProvider(IConfiguration configuration) => _configuration = configuration;

    public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken) =>
        Task.FromResult(_configuration[$"{ConfigurationSectionPath}:{secretReference}"]);
}
