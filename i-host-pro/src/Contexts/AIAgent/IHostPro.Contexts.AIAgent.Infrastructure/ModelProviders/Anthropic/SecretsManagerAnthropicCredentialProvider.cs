using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;

/// <summary>
/// AWS Secrets Manager-backed <see cref="IAnthropicCredentialProvider"/> —
/// registered for every non-Development environment (Fase 12, CP5.3A).
/// Resolves the secret id from configuration
/// (<c>AIAgent:Anthropic:Secrets:SecretsManagerSecretId</c>) rather than
/// hardcoding an environment-specific name/ARN. The fetched value is cached
/// for the process lifetime — mirrors <c>ConfigurationJwtSigningKeyProvider</c>'s
/// own singleton-caching precedent (ADR-012): a secret rotation requires an
/// ECS task redeploy/restart to take effect, a deliberate, documented
/// tradeoff for this checkpoint, not an oversight.
///
/// Fail-closed: ANY resolution problem (missing configuration, secret not
/// found, access denied, throttled, network error) is caught, logged
/// (exception/message only, never the secret value), and surfaced as a null
/// return — <see cref="AnthropicModelProvider"/> already turns a null/empty
/// key into a loud, permanent <c>ModelProviderException</c>, never a silent
/// fallback to a fake response. Never logs the resolved value.
/// </summary>
public sealed class SecretsManagerAnthropicCredentialProvider : IAnthropicCredentialProvider
{
    private const string SecretIdConfigurationKey = "AIAgent:Anthropic:Secrets:SecretsManagerSecretId";

    private readonly ISecretValueReader _secretValueReader;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SecretsManagerAnthropicCredentialProvider> _logger;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private string? _cachedApiKey;

    public SecretsManagerAnthropicCredentialProvider(
        ISecretValueReader secretValueReader,
        IConfiguration configuration,
        ILogger<SecretsManagerAnthropicCredentialProvider> logger)
    {
        _secretValueReader = secretValueReader;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        if (_cachedApiKey is not null)
            return _cachedApiKey;

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedApiKey is not null)
                return _cachedApiKey;

            var secretId = _configuration[SecretIdConfigurationKey];
            if (string.IsNullOrWhiteSpace(secretId))
            {
                _logger.LogError(
                    "{ConfigurationKey} is not configured - cannot resolve the Anthropic API key.",
                    SecretIdConfigurationKey);
                return null;
            }

            _cachedApiKey = await _secretValueReader.GetSecretStringAsync(secretId, cancellationToken);
            return _cachedApiKey;
        }
        catch (Exception ex)
        {
            // Deliberately broad: any failure to resolve the key (not found,
            // access denied, throttled, transient network error) must fail
            // closed the same way - never distinguish "not configured" from
            // "AWS is unreachable" by silently degrading differently.
            _logger.LogError(ex, "Failed to resolve the Anthropic API key from AWS Secrets Manager.");
            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
