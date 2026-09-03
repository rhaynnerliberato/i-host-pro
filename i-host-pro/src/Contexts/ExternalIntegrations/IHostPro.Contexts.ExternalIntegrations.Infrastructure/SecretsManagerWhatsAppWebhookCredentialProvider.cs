using IHostPro.Contexts.ExternalIntegrations.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// AWS Secrets Manager-backed <see cref="IWhatsAppWebhookCredentialProvider"/>
/// — registered for every non-Development environment (Fase 12, CP5.3A).
/// Resolves the App Secret and Verify Token from two separately configured
/// secret ids (<c>ExternalIntegrations:WhatsApp:Webhook:Secrets:AppSecretSecretsManagerSecretId</c>
/// / <c>...:VerifyTokenSecretsManagerSecretId</c>) — these are app/deployment-level
/// credentials (ADR-022 item 8/9), never a per-tenant reference, so a single
/// configured secret id per value is correct here (contrast with
/// <see cref="SecretsManagerWhatsAppCredentialProvider"/>, which is
/// per-tenant).
///
/// Each value is cached for the process lifetime once resolved (mirrors
/// AIAgent.Infrastructure's own <c>SecretsManagerAnthropicCredentialProvider</c>
/// precedent). Fail-closed: any resolution problem is caught, logged
/// (message only, never the secret value), and surfaced as a null return —
/// <c>WhatsAppWebhookController</c>/<c>MetaWebhookSignatureVerifier</c>
/// already treat a null App Secret/Verify Token as "webhook not configured",
/// never a silent bypass of signature verification. Never logs a resolved
/// value.
/// </summary>
public sealed class SecretsManagerWhatsAppWebhookCredentialProvider : IWhatsAppWebhookCredentialProvider
{
    private const string AppSecretIdConfigurationKey = "ExternalIntegrations:WhatsApp:Webhook:Secrets:AppSecretSecretsManagerSecretId";
    private const string VerifyTokenIdConfigurationKey = "ExternalIntegrations:WhatsApp:Webhook:Secrets:VerifyTokenSecretsManagerSecretId";

    private readonly ISecretValueReader _secretValueReader;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SecretsManagerWhatsAppWebhookCredentialProvider> _logger;

    private readonly SemaphoreSlim _appSecretLock = new(1, 1);
    private string? _cachedAppSecret;

    private readonly SemaphoreSlim _verifyTokenLock = new(1, 1);
    private string? _cachedVerifyToken;

    public SecretsManagerWhatsAppWebhookCredentialProvider(
        ISecretValueReader secretValueReader,
        IConfiguration configuration,
        ILogger<SecretsManagerWhatsAppWebhookCredentialProvider> logger)
    {
        _secretValueReader = secretValueReader;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<string?> GetAppSecretAsync(CancellationToken cancellationToken) =>
        ResolveAsync(AppSecretIdConfigurationKey, _appSecretLock, () => _cachedAppSecret, v => _cachedAppSecret = v, cancellationToken);

    public Task<string?> GetVerifyTokenAsync(CancellationToken cancellationToken) =>
        ResolveAsync(VerifyTokenIdConfigurationKey, _verifyTokenLock, () => _cachedVerifyToken, v => _cachedVerifyToken = v, cancellationToken);

    private async Task<string?> ResolveAsync(
        string configurationKey,
        SemaphoreSlim cacheLock,
        Func<string?> readCache,
        Action<string?> writeCache,
        CancellationToken cancellationToken)
    {
        if (readCache() is { } cached)
            return cached;

        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (readCache() is { } cachedAfterLock)
                return cachedAfterLock;

            var secretId = _configuration[configurationKey];
            if (string.IsNullOrWhiteSpace(secretId))
            {
                _logger.LogError("{ConfigurationKey} is not configured - cannot resolve the WhatsApp webhook credential.", configurationKey);
                return null;
            }

            var value = await _secretValueReader.GetSecretStringAsync(secretId, cancellationToken);
            writeCache(value);
            return value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve a WhatsApp webhook credential from AWS Secrets Manager (key: {ConfigurationKey}).", configurationKey);
            return null;
        }
        finally
        {
            cacheLock.Release();
        }
    }
}
