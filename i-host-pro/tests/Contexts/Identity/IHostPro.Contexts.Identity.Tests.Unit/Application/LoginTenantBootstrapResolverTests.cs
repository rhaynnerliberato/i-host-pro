using FluentAssertions;
using IHostPro.Contexts.Identity.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application;

public class LoginTenantBootstrapResolverTests
{
    private sealed class FakeTenantBootstrapReader : ITenantBootstrapReader
    {
        public ActiveTenant? TenantToReturn { get; set; }

        public Task<ActiveTenant?> GetActiveTenantBySlugAsync(string slug, CancellationToken cancellationToken) =>
            Task.FromResult(TenantToReturn);

        public Task<ActiveTenant?> GetActiveTenantByIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by LoginTenantBootstrapResolver.");
    }

    private sealed class RecordingDummyPasswordVerifier : IDummyPasswordVerifier
    {
        public int CallCount { get; private set; }
        public string? LastPassword { get; private set; }

        public void Verify(string submittedPassword)
        {
            CallCount++;
            LastPassword = submittedPassword;
        }
    }

    private sealed class RecordingLogger : ILogger<LoginTenantBootstrapResolver>
    {
        public bool WarningLogged { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                WarningLogged = true;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static LoginCommand Command(string tenantSlug, string password) =>
        new(tenantSlug, "user@example.com", password, new AuthenticationRequestContext(null, null, null));

    [Fact]
    public async Task ResolveTenantAsync_returns_the_tenant_id_when_the_slug_resolves()
    {
        var tenantId = Guid.NewGuid();
        var reader = new FakeTenantBootstrapReader { TenantToReturn = new ActiveTenant(tenantId, "acme") };
        var resolver = new LoginTenantBootstrapResolver(reader, new RecordingDummyPasswordVerifier(), NullLogger<LoginTenantBootstrapResolver>.Instance);

        var result = await resolver.ResolveTenantAsync(Command("acme", "whatever"), CancellationToken.None);

        result.Should().Be(tenantId);
    }

    [Fact]
    public async Task ResolveTenantAsync_returns_null_when_the_slug_does_not_resolve()
    {
        var reader = new FakeTenantBootstrapReader { TenantToReturn = null };
        var resolver = new LoginTenantBootstrapResolver(reader, new RecordingDummyPasswordVerifier(), NullLogger<LoginTenantBootstrapResolver>.Instance);

        var result = await resolver.ResolveTenantAsync(Command("no-such-tenant", "whatever"), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTenantAsync_runs_the_dummy_password_verification_when_the_tenant_does_not_resolve()
    {
        var reader = new FakeTenantBootstrapReader { TenantToReturn = null };
        var dummyVerifier = new RecordingDummyPasswordVerifier();
        var resolver = new LoginTenantBootstrapResolver(reader, dummyVerifier, NullLogger<LoginTenantBootstrapResolver>.Instance);

        await resolver.ResolveTenantAsync(Command("no-such-tenant", "the-submitted-password"), CancellationToken.None);

        dummyVerifier.CallCount.Should().Be(1);
        dummyVerifier.LastPassword.Should().Be("the-submitted-password");
    }

    [Fact]
    public async Task ResolveTenantAsync_does_not_run_the_dummy_verification_when_the_tenant_resolves()
    {
        var reader = new FakeTenantBootstrapReader { TenantToReturn = new ActiveTenant(Guid.NewGuid(), "acme") };
        var dummyVerifier = new RecordingDummyPasswordVerifier();
        var resolver = new LoginTenantBootstrapResolver(reader, dummyVerifier, NullLogger<LoginTenantBootstrapResolver>.Instance);

        await resolver.ResolveTenantAsync(Command("acme", "whatever"), CancellationToken.None);

        dummyVerifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveTenantAsync_logs_a_warning_for_telemetry_when_the_tenant_does_not_resolve()
    {
        var reader = new FakeTenantBootstrapReader { TenantToReturn = null };
        var logger = new RecordingLogger();
        var resolver = new LoginTenantBootstrapResolver(reader, new RecordingDummyPasswordVerifier(), logger);

        await resolver.ResolveTenantAsync(Command("no-such-tenant", "whatever"), CancellationToken.None);

        logger.WarningLogged.Should().BeTrue();
    }
}
