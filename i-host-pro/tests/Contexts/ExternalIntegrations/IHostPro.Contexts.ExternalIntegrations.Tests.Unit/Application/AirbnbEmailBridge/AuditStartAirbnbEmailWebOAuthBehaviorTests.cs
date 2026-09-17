using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>Mirrors <c>AuditConnectAirbnbEmailMailboxBehaviorTests</c>'s own RecordingLogger pattern.</summary>
public class AuditStartAirbnbEmailWebOAuthBehaviorTests
{
    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<AuditStartAirbnbEmailWebOAuthBehavior>
    {
        public List<LoggedEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? throw new InvalidOperationException("Expected structured log state.");
            Entries.Add(new LoggedEntry(logLevel, exception, values));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static StartAirbnbEmailWebOAuthCommand Command() => new(TenantId, ActorUserId);

    [Fact]
    public async Task A_successful_start_logs_exactly_one_structured_information_entry_without_the_authorization_url()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditStartAirbnbEmailWebOAuthBehavior(new FixedTimeProvider(Now), logger);
        const string sensitiveUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=SENTINEL_STATE&code_challenge=SENTINEL_CHALLENGE";

        var result = await behavior.Handle(
            Command(),
            (_, _) => ValueTask.FromResult(Result.Success(new AirbnbEmailWebOAuthStartResult(sensitiveUrl))),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);

        var state = entry.State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["TenantId"].Should().Be(TenantId);
        state["ActorUserId"].Should().Be(ActorUserId);
        state["Result"].Should().Be("Success");

        var allValues = entry.State.Select(kvp => kvp.Value?.ToString() ?? string.Empty);
        foreach (var value in allValues)
        {
            value.Should().NotContain("SENTINEL_STATE");
            value.Should().NotContain("SENTINEL_CHALLENGE");
        }
    }

    [Fact]
    public async Task A_rejected_start_logs_the_error_code()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditStartAirbnbEmailWebOAuthBehavior(new FixedTimeProvider(Now), logger);
        var error = new Error(AirbnbEmailBridgeErrorCodes.WebOAuthNotConfigured, AirbnbEmailBridgeErrorCodes.WebOAuthNotConfigured);

        var result = await behavior.Handle(
            Command(), (_, _) => ValueTask.FromResult(Result.Failure<AirbnbEmailWebOAuthStartResult>(error)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        logger.Entries.Should().ContainSingle();
        var state = logger.Entries[0].State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["Result"].Should().Be("Rejected");
    }

    [Fact]
    public async Task A_thrown_exception_logs_and_rethrows_without_swallowing()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditStartAirbnbEmailWebOAuthBehavior(new FixedTimeProvider(Now), logger);
        var failure = new InvalidOperationException("simulated failure");

        var act = async () => await behavior.Handle(Command(), (_, _) => throw failure, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Error);
    }
}
