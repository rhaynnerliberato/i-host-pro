using FluentAssertions;
using IHostPro.Contexts.Identity.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application;

public class RefreshTokenTenantBootstrapResolverTests
{
    private sealed class FakeParser : IRefreshTokenParser
    {
        public ParsedRefreshToken? Result { get; set; }

        public bool TryParse(string? presentedToken, out ParsedRefreshToken parsed)
        {
            if (Result is null)
            {
                parsed = default;
                return false;
            }

            parsed = Result.Value;
            return true;
        }
    }

    private sealed class FakeTenantBootstrapReader : ITenantBootstrapReader
    {
        public ActiveTenant? TenantToReturn { get; set; }

        public Task<ActiveTenant?> GetActiveTenantBySlugAsync(string slug, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by RefreshTokenTenantBootstrapResolver.");

        public Task<ActiveTenant?> GetActiveTenantByIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(TenantToReturn);
    }

    private sealed class RecordingLogger : ILogger<RefreshTokenTenantBootstrapResolver>
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

    private static RefreshTokenCommand Command(string token = "whatever") =>
        new(token, new AuthenticationRequestContext(null, null, null));

    [Fact]
    public async Task ResolveTenantAsync_returns_null_and_never_looks_up_a_tenant_when_the_token_is_malformed()
    {
        var parser = new FakeParser { Result = null };
        var reader = new FakeTenantBootstrapReader { TenantToReturn = new ActiveTenant(Guid.NewGuid(), "acme") };
        var resolver = new RefreshTokenTenantBootstrapResolver(parser, reader, NullLogger<RefreshTokenTenantBootstrapResolver>.Instance);

        var result = await resolver.ResolveTenantAsync(Command(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTenantAsync_returns_the_tenant_id_when_the_embedded_tenant_resolves()
    {
        var tenantId = Guid.NewGuid();
        var parser = new FakeParser { Result = new ParsedRefreshToken(tenantId, Guid.NewGuid()) };
        var reader = new FakeTenantBootstrapReader { TenantToReturn = new ActiveTenant(tenantId, "acme") };
        var resolver = new RefreshTokenTenantBootstrapResolver(parser, reader, NullLogger<RefreshTokenTenantBootstrapResolver>.Instance);

        var result = await resolver.ResolveTenantAsync(Command(), CancellationToken.None);

        result.Should().Be(tenantId);
    }

    [Fact]
    public async Task ResolveTenantAsync_returns_null_when_the_embedded_tenant_does_not_resolve()
    {
        var parser = new FakeParser { Result = new ParsedRefreshToken(Guid.NewGuid(), Guid.NewGuid()) };
        var reader = new FakeTenantBootstrapReader { TenantToReturn = null };
        var resolver = new RefreshTokenTenantBootstrapResolver(parser, reader, NullLogger<RefreshTokenTenantBootstrapResolver>.Instance);

        var result = await resolver.ResolveTenantAsync(Command(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTenantAsync_logs_a_warning_for_telemetry_on_a_malformed_token()
    {
        var parser = new FakeParser { Result = null };
        var reader = new FakeTenantBootstrapReader();
        var logger = new RecordingLogger();
        var resolver = new RefreshTokenTenantBootstrapResolver(parser, reader, logger);

        await resolver.ResolveTenantAsync(Command(), CancellationToken.None);

        logger.WarningLogged.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveTenantAsync_logs_a_warning_for_telemetry_when_the_embedded_tenant_does_not_resolve()
    {
        var parser = new FakeParser { Result = new ParsedRefreshToken(Guid.NewGuid(), Guid.NewGuid()) };
        var reader = new FakeTenantBootstrapReader { TenantToReturn = null };
        var logger = new RecordingLogger();
        var resolver = new RefreshTokenTenantBootstrapResolver(parser, reader, logger);

        await resolver.ResolveTenantAsync(Command(), CancellationToken.None);

        logger.WarningLogged.Should().BeTrue();
    }
}
