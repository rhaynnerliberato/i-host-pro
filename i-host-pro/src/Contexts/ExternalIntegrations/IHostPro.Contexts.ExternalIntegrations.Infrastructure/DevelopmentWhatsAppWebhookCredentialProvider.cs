using IHostPro.Contexts.ExternalIntegrations.Application;
using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// Development-only <see cref="IWhatsAppWebhookCredentialProvider"/> —
/// resolves the App Secret/Verify Token from <see cref="IConfiguration"/>
/// (User Secrets/environment variables in Development, never a committed
/// <c>appsettings.json</c> value), mirroring <c>IJwtSigningKeyProvider</c>'s
/// own Development implementation precedent (ADR-012) and
/// <see cref="DevelopmentWhatsAppCredentialProvider"/>'s exact pattern.
/// Registered only when <c>IsDevelopment()</c> (see
/// <c>ExternalIntegrationsModuleExtensions</c>) — never registered for any
/// other environment, so resolving <see cref="IWhatsAppWebhookCredentialProvider"/>
/// outside Development fails loudly (no implementation registered) instead of
/// silently falling back to this one (ADR-022, item 8: Production storage
/// remains an open decision, never a silent Development fallback).
///
/// Never logs the resolved value.
/// </summary>
public sealed class DevelopmentWhatsAppWebhookCredentialProvider : IWhatsAppWebhookCredentialProvider
{
    private const string AppSecretConfigurationPath = "ExternalIntegrations:WhatsApp:Webhook:AppSecret";
    private const string VerifyTokenConfigurationPath = "ExternalIntegrations:WhatsApp:Webhook:VerifyToken";

    private readonly IConfiguration _configuration;

    public DevelopmentWhatsAppWebhookCredentialProvider(IConfiguration configuration) => _configuration = configuration;

    public Task<string?> GetAppSecretAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_configuration[AppSecretConfigurationPath]);

    public Task<string?> GetVerifyTokenAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_configuration[VerifyTokenConfigurationPath]);
}
