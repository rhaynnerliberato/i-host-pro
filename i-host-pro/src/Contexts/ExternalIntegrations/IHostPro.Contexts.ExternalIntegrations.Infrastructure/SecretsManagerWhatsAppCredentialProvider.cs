using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.ExternalIntegrations.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// AWS Secrets Manager-backed <see cref="IWhatsAppCredentialProvider"/> for
/// per-tenant WhatsApp credentials (Fase 12, CP5.3A Decision Gate:
/// <c>WhatsAppTenantSecretBackend=AWS_SECRETS_MANAGER_PER_TENANT</c>) —
/// registered for every non-Development environment.
///
/// <c>secretReference</c> (as stored on <c>WhatsAppIntegration</c> and
/// supplied via <c>ConfigureWhatsAppIntegrationCommand</c>) is NEVER used
/// directly as an AWS Secrets Manager SecretId: it is validated against a
/// strict safe charset (see <see cref="SafeReferencePattern"/>) and then
/// used only as a SUFFIX under a secret id namespace this provider itself
/// constructs from <see cref="ICurrentTenantProvider.TenantId"/> — the
/// already-authenticated, RLS-resolved tenant, never a value an admin/tenant
/// could directly control. This closes the "a tenant-controlled string
/// resolves to an arbitrary ARN" risk explicitly called out in this
/// checkpoint's mandate: no caller-supplied string ever becomes the whole
/// SecretId, only a validated suffix within a tenant-scoped prefix.
///
/// The secret id namespace prefix is configuration-driven
/// (<c>ExternalIntegrations:WhatsApp:Secrets:SecretsManagerSecretPrefix</c>,
/// e.g. <c>ihostpro/homolog/tenants</c>) — never hardcoded per environment.
/// Resolved values are cached per constructed secret id for the process
/// lifetime (a rotation requires a task redeploy/restart, same documented
/// tradeoff as the other Secrets Manager-backed providers in this
/// checkpoint). Fail-closed: any resolution problem returns null, never a
/// thrown AWS SDK exception leaking into Application/Domain. Never logs a
/// resolved value.
/// </summary>
public sealed partial class SecretsManagerWhatsAppCredentialProvider : IWhatsAppCredentialProvider
{
    private const string SecretPrefixConfigurationKey = "ExternalIntegrations:WhatsApp:Secrets:SecretsManagerSecretPrefix";

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,100}$")]
    private static partial Regex SafeReferencePattern();

    private readonly ISecretValueReader _secretValueReader;
    private readonly IConfiguration _configuration;
    private readonly ICurrentTenantProvider _currentTenantProvider;
    private readonly ILogger<SecretsManagerWhatsAppCredentialProvider> _logger;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public SecretsManagerWhatsAppCredentialProvider(
        ISecretValueReader secretValueReader,
        IConfiguration configuration,
        ICurrentTenantProvider currentTenantProvider,
        ILogger<SecretsManagerWhatsAppCredentialProvider> logger)
    {
        _secretValueReader = secretValueReader;
        _configuration = configuration;
        _currentTenantProvider = currentTenantProvider;
        _logger = logger;
    }

    public async Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretReference) || !SafeReferencePattern().IsMatch(secretReference))
        {
            _logger.LogError("WhatsApp secret reference has an invalid format and was rejected before any AWS Secrets Manager call.");
            return null;
        }

        var prefix = _configuration[SecretPrefixConfigurationKey];
        if (string.IsNullOrWhiteSpace(prefix))
        {
            _logger.LogError("{ConfigurationKey} is not configured - cannot resolve tenant WhatsApp credentials.", SecretPrefixConfigurationKey);
            return null;
        }

        var secretId = $"{prefix}/{_currentTenantProvider.TenantId:D}/whatsapp/{secretReference}";

        if (_cache.TryGetValue(secretId, out var cached))
            return cached;

        try
        {
            var value = await _secretValueReader.GetSecretStringAsync(secretId, cancellationToken);
            _cache[secretId] = value;
            return value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve a tenant WhatsApp credential from AWS Secrets Manager.");
            return null;
        }
    }
}
