using FluentAssertions;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.ModelProviders.Anthropic;

/// <summary>
/// Fase 12, Checkpoint 5.3A — deterministic tests for
/// <see cref="SecretsManagerAnthropicCredentialProvider"/> against a
/// hand-rolled <see cref="FakeSecretValueReader"/> (never a real AWS call).
/// Proves fail-closed behavior (missing configuration, AWS Secrets Manager
/// failure), process-lifetime caching, and that the resolved secret value is
/// never logged.
/// </summary>
public class SecretsManagerAnthropicCredentialProviderTests
{
    private const string SecretIdConfigurationKey = "AIAgent:Anthropic:Secrets:SecretsManagerSecretId";
    private const string ConfiguredSecretId = "ihostpro/homolog/anthropic";
    private const string SentinelApiKey = "sk-ant-SENTINEL_never_logged";

    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<SecretsManagerAnthropicCredentialProvider>
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
        private readonly string? _value;
        private readonly Exception? _throws;

        public int CallCount { get; private set; }
        public string? LastRequestedSecretId { get; private set; }

        private FakeSecretValueReader(string? value, Exception? throws)
        {
            _value = value;
            _throws = throws;
        }

        public static FakeSecretValueReader Returning(string value) => new(value, null);
        public static FakeSecretValueReader Throwing(Exception exception) => new(null, exception);

        public Task<string> GetSecretStringAsync(string secretId, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestedSecretId = secretId;
            return _throws is not null ? Task.FromException<string>(_throws) : Task.FromResult(_value!);
        }
    }

    private static IConfiguration BuildConfiguration(string? secretId = ConfiguredSecretId) =>
        new ConfigurationBuilder().AddInMemoryCollection(secretId is null
            ? []
            : new Dictionary<string, string?> { [SecretIdConfigurationKey] = secretId })
            .Build();

    [Fact]
    public async Task GetApiKeyAsync_returns_the_value_resolved_by_the_secret_reader()
    {
        var reader = FakeSecretValueReader.Returning(SentinelApiKey);
        var provider = new SecretsManagerAnthropicCredentialProvider(reader, BuildConfiguration(), new RecordingLogger());

        var result = await provider.GetApiKeyAsync(CancellationToken.None);

        result.Should().Be(SentinelApiKey);
        reader.LastRequestedSecretId.Should().Be(ConfiguredSecretId);
    }

    [Fact]
    public async Task GetApiKeyAsync_fails_closed_returning_null_when_the_secret_id_is_not_configured()
    {
        var reader = FakeSecretValueReader.Returning(SentinelApiKey);
        var provider = new SecretsManagerAnthropicCredentialProvider(reader, BuildConfiguration(secretId: null), new RecordingLogger());

        var result = await provider.GetApiKeyAsync(CancellationToken.None);

        result.Should().BeNull();
        reader.CallCount.Should().Be(0, "an unconfigured secret id must never reach AWS Secrets Manager");
    }

    [Fact]
    public async Task GetApiKeyAsync_fails_closed_returning_null_when_the_secret_reader_throws()
    {
        var reader = FakeSecretValueReader.Throwing(new InvalidOperationException("secret not found"));
        var provider = new SecretsManagerAnthropicCredentialProvider(reader, BuildConfiguration(), new RecordingLogger());

        var result = await provider.GetApiKeyAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetApiKeyAsync_caches_the_resolved_value_and_never_calls_the_reader_twice()
    {
        var reader = FakeSecretValueReader.Returning(SentinelApiKey);
        var provider = new SecretsManagerAnthropicCredentialProvider(reader, BuildConfiguration(), new RecordingLogger());

        await provider.GetApiKeyAsync(CancellationToken.None);
        await provider.GetApiKeyAsync(CancellationToken.None);
        var third = await provider.GetApiKeyAsync(CancellationToken.None);

        third.Should().Be(SentinelApiKey);
        reader.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetApiKeyAsync_never_logs_the_resolved_secret_value()
    {
        var reader = FakeSecretValueReader.Returning(SentinelApiKey);
        var logger = new RecordingLogger();
        var provider = new SecretsManagerAnthropicCredentialProvider(reader, BuildConfiguration(), logger);

        await provider.GetApiKeyAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(entry =>
            entry.State.Any(kv => kv.Value != null && kv.Value.ToString() == SentinelApiKey));
    }

    [Fact]
    public async Task GetApiKeyAsync_logs_the_failure_without_the_secret_id_leaking_a_secret_when_the_reader_throws()
    {
        var reader = FakeSecretValueReader.Throwing(new InvalidOperationException("boom"));
        var logger = new RecordingLogger();
        var provider = new SecretsManagerAnthropicCredentialProvider(reader, BuildConfiguration(), logger);

        await provider.GetApiKeyAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }
}
