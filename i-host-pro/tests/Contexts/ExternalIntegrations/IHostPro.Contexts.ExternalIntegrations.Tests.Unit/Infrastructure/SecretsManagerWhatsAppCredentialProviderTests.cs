using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;

/// <summary>
/// Fase 12, Checkpoint 5.3A — deterministic tests for
/// <see cref="SecretsManagerWhatsAppCredentialProvider"/> (per-tenant AWS
/// Secrets Manager backend, <c>WhatsAppTenantSecretBackend=AWS_SECRETS_MANAGER_PER_TENANT</c>)
/// against a hand-rolled <see cref="FakeSecretValueReader"/>. Proves: the
/// tenant-controlled <c>secretReference</c> is validated and used only as a
/// suffix under a tenant-scoped prefix (never becomes an arbitrary AWS
/// SecretId), cross-tenant isolation (the same reference resolves a
/// different secret id per tenant), fail-closed behavior, and that resolved
/// values are never logged.
/// </summary>
public class SecretsManagerWhatsAppCredentialProviderTests
{
    private const string PrefixConfigurationKey = "ExternalIntegrations:WhatsApp:Secrets:SecretsManagerSecretPrefix";
    private const string ConfiguredPrefix = "ihostpro/homolog/tenants";
    private const string SentinelToken = "SENTINEL_ACCESS_TOKEN_never_logged";

    private sealed class FakeCurrentTenantProvider(Guid tenantId) : ICurrentTenantProvider
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<SecretsManagerWhatsAppCredentialProvider>
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

        public List<string> RequestedSecretIds { get; } = [];

        public FakeSecretValueReader WithValue(string secretId, string value)
        {
            _values[secretId] = value;
            return this;
        }

        public Task<string> GetSecretStringAsync(string secretId, CancellationToken cancellationToken)
        {
            RequestedSecretIds.Add(secretId);
            return _values.TryGetValue(secretId, out var value)
                ? Task.FromResult(value)
                : Task.FromException<string>(new InvalidOperationException($"secret '{secretId}' not found"));
        }
    }

    private static IConfiguration BuildConfiguration(string? prefix = ConfiguredPrefix) =>
        new ConfigurationBuilder().AddInMemoryCollection(prefix is null
            ? []
            : new Dictionary<string, string?> { [PrefixConfigurationKey] = prefix })
            .Build();

    private static SecretsManagerWhatsAppCredentialProvider BuildProvider(
        FakeSecretValueReader reader, Guid tenantId, IConfiguration? configuration = null, RecordingLogger? logger = null) =>
        new(reader, configuration ?? BuildConfiguration(), new FakeCurrentTenantProvider(tenantId), logger ?? new RecordingLogger());

    [Fact]
    public async Task GetSecretAsync_resolves_the_secret_scoped_under_the_current_tenant_and_prefix()
    {
        var tenantId = Guid.NewGuid();
        var expectedSecretId = $"{ConfiguredPrefix}/{tenantId:D}/whatsapp/access-token";
        var reader = new FakeSecretValueReader().WithValue(expectedSecretId, SentinelToken);
        var provider = BuildProvider(reader, tenantId);

        var result = await provider.GetSecretAsync("access-token", CancellationToken.None);

        result.Should().Be(SentinelToken);
        reader.RequestedSecretIds.Should().ContainSingle().Which.Should().Be(expectedSecretId);
    }

    [Fact]
    public async Task GetSecretAsync_never_lets_the_reference_string_become_the_whole_secret_id()
    {
        var tenantId = Guid.NewGuid();
        // Even if a reference happened to look like a full ARN/path, it must
        // only ever be appended as the final path segment under the
        // tenant-scoped prefix - it can never replace or escape that prefix.
        var reader = new FakeSecretValueReader();
        var provider = BuildProvider(reader, tenantId);

        await provider.GetSecretAsync("access-token", CancellationToken.None);

        reader.RequestedSecretIds.Single().Should().StartWith(ConfiguredPrefix + "/" + tenantId.ToString("D") + "/whatsapp/");
    }

    [Theory]
    [InlineData("../../other-tenant/secret")]
    [InlineData("arn:aws:secretsmanager:sa-east-1:816435462760:secret:ihostpro/production/jwt")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task GetSecretAsync_rejects_malformed_references_before_any_AWS_call(string malformedReference)
    {
        var reader = new FakeSecretValueReader();
        var provider = BuildProvider(reader, Guid.NewGuid());

        var result = await provider.GetSecretAsync(malformedReference, CancellationToken.None);

        result.Should().BeNull();
        reader.RequestedSecretIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSecretAsync_isolates_tenants_the_same_reference_resolves_a_different_secret_id_per_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var secretIdForA = $"{ConfiguredPrefix}/{tenantA:D}/whatsapp/access-token";
        var secretIdForB = $"{ConfiguredPrefix}/{tenantB:D}/whatsapp/access-token";

        var reader = new FakeSecretValueReader()
            .WithValue(secretIdForA, "TOKEN_FOR_TENANT_A")
            .WithValue(secretIdForB, "TOKEN_FOR_TENANT_B");

        var resultA = await BuildProvider(reader, tenantA).GetSecretAsync("access-token", CancellationToken.None);
        var resultB = await BuildProvider(reader, tenantB).GetSecretAsync("access-token", CancellationToken.None);

        resultA.Should().Be("TOKEN_FOR_TENANT_A");
        resultB.Should().Be("TOKEN_FOR_TENANT_B");
        resultA.Should().NotBe(resultB);
    }

    [Fact]
    public async Task GetSecretAsync_fails_closed_when_the_prefix_is_not_configured()
    {
        var reader = new FakeSecretValueReader();
        var provider = BuildProvider(reader, Guid.NewGuid(), BuildConfiguration(prefix: null));

        var result = await provider.GetSecretAsync("access-token", CancellationToken.None);

        result.Should().BeNull();
        reader.RequestedSecretIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSecretAsync_fails_closed_when_the_secret_does_not_exist()
    {
        var reader = new FakeSecretValueReader(); // no values configured - GetSecretStringAsync throws
        var provider = BuildProvider(reader, Guid.NewGuid());

        var result = await provider.GetSecretAsync("access-token", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSecretAsync_never_logs_the_resolved_secret_value()
    {
        var tenantId = Guid.NewGuid();
        var secretId = $"{ConfiguredPrefix}/{tenantId:D}/whatsapp/access-token";
        var reader = new FakeSecretValueReader().WithValue(secretId, SentinelToken);
        var logger = new RecordingLogger();
        var provider = BuildProvider(reader, tenantId, logger: logger);

        await provider.GetSecretAsync("access-token", CancellationToken.None);

        logger.Entries.Should().NotContain(entry => entry.State.Any(kv => kv.Value != null && kv.Value.ToString() == SentinelToken));
    }
}
