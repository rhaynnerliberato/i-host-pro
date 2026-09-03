using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;

/// <summary>
/// Fase 12, Checkpoint 5.3A — deterministic tests for
/// <see cref="SecretsManagerWhatsAppWebhookCredentialProvider"/> against a
/// hand-rolled <see cref="FakeSecretValueReader"/> (never a real AWS call).
/// Proves the App Secret and Verify Token resolve independently, fail
/// closed on missing configuration/AWS failure, cache per-value, and never
/// log a resolved secret value.
/// </summary>
public class SecretsManagerWhatsAppWebhookCredentialProviderTests
{
    private const string AppSecretConfigurationKey = "ExternalIntegrations:WhatsApp:Webhook:Secrets:AppSecretSecretsManagerSecretId";
    private const string VerifyTokenConfigurationKey = "ExternalIntegrations:WhatsApp:Webhook:Secrets:VerifyTokenSecretsManagerSecretId";
    private const string ConfiguredAppSecretId = "ihostpro/homolog/meta/webhook/app-secret";
    private const string ConfiguredVerifyTokenId = "ihostpro/homolog/meta/webhook/verify-token";
    private const string SentinelAppSecret = "SENTINEL_APP_SECRET_never_logged";
    private const string SentinelVerifyToken = "SENTINEL_VERIFY_TOKEN_never_logged";

    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<SecretsManagerWhatsAppWebhookCredentialProvider>
    {
        public List<LoggedEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? throw new InvalidOperationException("Expected structured log state (a message template with named placeholders).");
            Entries.Add(new LoggedEntry(logLevel, exception, values));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FakeSecretValueReader : ISecretValueReader
    {
        private readonly Dictionary<string, string> _values = [];
        private readonly HashSet<string> _throwFor = [];

        public int CallCount { get; private set; }

        public FakeSecretValueReader WithValue(string secretId, string value)
        {
            _values[secretId] = value;
            return this;
        }

        public FakeSecretValueReader ThrowingFor(string secretId)
        {
            _throwFor.Add(secretId);
            return this;
        }

        public Task<string> GetSecretStringAsync(string secretId, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_throwFor.Contains(secretId))
                return Task.FromException<string>(new InvalidOperationException($"secret '{secretId}' not found"));

            return _values.TryGetValue(secretId, out var value)
                ? Task.FromResult(value)
                : Task.FromException<string>(new InvalidOperationException($"unexpected secret id '{secretId}'"));
        }
    }

    private static IConfiguration BuildConfiguration(string? appSecretId = ConfiguredAppSecretId, string? verifyTokenId = ConfiguredVerifyTokenId)
    {
        var values = new Dictionary<string, string?>();
        if (appSecretId is not null) values[AppSecretConfigurationKey] = appSecretId;
        if (verifyTokenId is not null) values[VerifyTokenConfigurationKey] = verifyTokenId;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static SecretsManagerWhatsAppWebhookCredentialProvider BuildProvider(
        FakeSecretValueReader reader, IConfiguration? configuration = null, RecordingLogger? logger = null) =>
        new(reader, configuration ?? BuildConfiguration(), logger ?? new RecordingLogger());

    [Fact]
    public async Task GetAppSecretAsync_and_GetVerifyTokenAsync_resolve_independently()
    {
        var reader = new FakeSecretValueReader()
            .WithValue(ConfiguredAppSecretId, SentinelAppSecret)
            .WithValue(ConfiguredVerifyTokenId, SentinelVerifyToken);
        var provider = BuildProvider(reader);

        var appSecret = await provider.GetAppSecretAsync(CancellationToken.None);
        var verifyToken = await provider.GetVerifyTokenAsync(CancellationToken.None);

        appSecret.Should().Be(SentinelAppSecret);
        verifyToken.Should().Be(SentinelVerifyToken);
    }

    [Fact]
    public async Task GetAppSecretAsync_fails_closed_when_not_configured()
    {
        var reader = new FakeSecretValueReader().WithValue(ConfiguredAppSecretId, SentinelAppSecret);
        var provider = BuildProvider(reader, BuildConfiguration(appSecretId: null));

        var result = await provider.GetAppSecretAsync(CancellationToken.None);

        result.Should().BeNull();
        reader.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetVerifyTokenAsync_fails_closed_when_the_secret_reader_throws()
    {
        var reader = new FakeSecretValueReader().ThrowingFor(ConfiguredVerifyTokenId);
        var provider = BuildProvider(reader);

        var result = await provider.GetVerifyTokenAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Each_value_is_cached_independently_and_the_reader_is_called_once_per_value()
    {
        var reader = new FakeSecretValueReader()
            .WithValue(ConfiguredAppSecretId, SentinelAppSecret)
            .WithValue(ConfiguredVerifyTokenId, SentinelVerifyToken);
        var provider = BuildProvider(reader);

        await provider.GetAppSecretAsync(CancellationToken.None);
        await provider.GetAppSecretAsync(CancellationToken.None);
        await provider.GetVerifyTokenAsync(CancellationToken.None);
        await provider.GetVerifyTokenAsync(CancellationToken.None);

        reader.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Resolved_secret_values_are_never_logged()
    {
        var reader = new FakeSecretValueReader()
            .WithValue(ConfiguredAppSecretId, SentinelAppSecret)
            .WithValue(ConfiguredVerifyTokenId, SentinelVerifyToken);
        var logger = new RecordingLogger();
        var provider = BuildProvider(reader, logger: logger);

        await provider.GetAppSecretAsync(CancellationToken.None);
        await provider.GetVerifyTokenAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(entry => entry.State.Any(kv =>
            kv.Value != null && (kv.Value.ToString() == SentinelAppSecret || kv.Value.ToString() == SentinelVerifyToken)));
    }
}
