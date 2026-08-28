using IHostPro.Contexts.PropertyManagement.Application;
using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure;

/// <summary>
/// Development-only <see cref="IPropertyAccessCredentialProvider"/> —
/// resolves a secret reference from <see cref="IConfiguration"/> (User
/// Secrets/environment variables in Development, never a committed
/// <c>appsettings.json</c> value), mirroring
/// <c>DevelopmentWhatsAppCredentialProvider</c>'s own precedent (ADR-012
/// origin). Registered only when <c>IsDevelopment()</c> (see
/// <c>PropertyManagementModuleExtensions</c>) — never registered for any
/// other environment, so resolving <see cref="IPropertyAccessCredentialProvider"/>
/// outside Development fails loudly (no implementation registered) instead
/// of silently falling back to this one (CP6.1 Decision Gate item 9:
/// Production must never be faked by falling back to Development
/// configuration; <c>ProductionAccessCredentialSecretBackendAvailable=false</c>).
///
/// Never logs the resolved value.
/// </summary>
public sealed class DevelopmentPropertyAccessCredentialProvider : IPropertyAccessCredentialProvider
{
    private const string ConfigurationSectionPath = "PropertyManagement:GuestAccess:Secrets";

    private readonly IConfiguration _configuration;

    public DevelopmentPropertyAccessCredentialProvider(IConfiguration configuration) => _configuration = configuration;

    public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken) =>
        Task.FromResult(_configuration[$"{ConfigurationSectionPath}:{secretReference}"]);
}
