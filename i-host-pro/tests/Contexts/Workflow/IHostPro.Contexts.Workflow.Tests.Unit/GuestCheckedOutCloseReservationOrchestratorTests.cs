using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Workflow.Application;
using IHostPro.Contexts.Workflow.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Workflow.Tests.Unit;

/// <summary>
/// Fase 10, Checkpoint 1 (Guest Operations Foundation) — mirrors
/// <see cref="ReservationCreatedCleaningOrchestratorTests"/>'s own structure
/// exactly, for Workflow's second trigger→action use case: a
/// <see cref="GuestCheckedOut"/> input must always translate into EXACTLY
/// one dispatched <see cref="CloseReservation"/> command, with the minimal,
/// non-PII payload the user-approved closure semantics require.
/// </summary>
public class GuestCheckedOutCloseReservationOrchestratorTests
{
    private sealed class FakeWorkflowCommandDispatcher : IWorkflowCommandDispatcher
    {
        public List<CloseReservation> DispatchedCommands { get; } = [];

        /// <summary>When set, the dispatch throws this instead of recording the command — simulates a transport failure without a real, destructive RabbitMQ outage.</summary>
        public Exception? FailWith { get; set; }

        public Task DispatchCreateCleaningForReservationAsync(
            IHostPro.Contexts.Housekeeping.Contracts.CreateCleaningForReservation command, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test class.");

        public Task DispatchCloseReservationAsync(CloseReservation command, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
                throw FailWith;

            DispatchedCommands.Add(command);
            return Task.CompletedTask;
        }

        // Not exercised by this test class (only GuestCheckedOut ->
        // CloseReservation is in scope here) — required only to satisfy
        // IWorkflowCommandDispatcher after Fase 10, Checkpoint 3 added the
        // two reschedule dispatch methods. See
        // EarlyCheckinApprovedRescheduleOrchestratorTests/
        // LateCheckoutApprovedRescheduleOrchestratorTests for their own
        // dedicated fake/coverage.
        public Task DispatchRescheduleForEarlyCheckInAsync(
            RescheduleReservationForEarlyCheckIn command, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DispatchRescheduleForLateCheckoutAsync(
            RescheduleReservationForLateCheckout command, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<GuestCheckedOutCloseReservationOrchestrator>
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

    private static GuestCheckedOut Event(Guid tenantId, Guid reservationId, Guid correlationId) => new()
    {
        TenantId = tenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "GuestStayOperation",
        CorrelationId = correlationId,
        ActorType = "System",
        ReservationId = reservationId,
    };

    private static GuestCheckedOutCloseReservationOrchestrator Orchestrator(
        IWorkflowCommandDispatcher dispatcher,
        ILogger<GuestCheckedOutCloseReservationOrchestrator>? logger = null,
        TimeProvider? timeProvider = null) =>
        new(dispatcher, timeProvider ?? TimeProvider.System, logger ?? NullLogger<GuestCheckedOutCloseReservationOrchestrator>.Instance);

    [Fact]
    public async Task A_GuestCheckedOut_event_dispatches_exactly_one_command_with_the_translated_identifiers()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var @event = Event(tenantId, reservationId, correlationId);

        var dispatcher = new FakeWorkflowCommandDispatcher();
        var orchestrator = Orchestrator(dispatcher);

        await orchestrator.HandleAsync(@event, CancellationToken.None);

        dispatcher.DispatchedCommands.Should().ContainSingle();
        var command = dispatcher.DispatchedCommands[0];
        command.TenantId.Should().Be(tenantId);
        command.ReservationId.Should().Be(reservationId);
        command.CorrelationId.Should().Be(correlationId);
        command.CausationId.Should().Be(@event.EventId);
    }

    [Fact]
    public async Task The_dispatched_command_carries_no_PII()
    {
        var @event = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dispatcher = new FakeWorkflowCommandDispatcher();
        var orchestrator = Orchestrator(dispatcher);

        await orchestrator.HandleAsync(@event, CancellationToken.None);

        // Structural proof by construction: CloseReservation has exactly
        // four properties (TenantId/ReservationId/CorrelationId/CausationId)
        // — no guest name/phone, no PropertyId (Reservations already owns it).
        var properties = typeof(CloseReservation).GetProperties();
        properties.Select(p => p.Name).Should().BeEquivalentTo(
            "TenantId", "ReservationId", "CorrelationId", "CausationId");
    }

    [Fact]
    public async Task Two_different_GuestCheckedOut_events_each_dispatch_their_own_independent_command()
    {
        var dispatcher = new FakeWorkflowCommandDispatcher();
        var orchestrator = Orchestrator(dispatcher);

        var first = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var second = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await orchestrator.HandleAsync(first, CancellationToken.None);
        await orchestrator.HandleAsync(second, CancellationToken.None);

        dispatcher.DispatchedCommands.Should().HaveCount(2);
        dispatcher.DispatchedCommands[0].ReservationId.Should().Be(first.ReservationId);
        dispatcher.DispatchedCommands[1].ReservationId.Should().Be(second.ReservationId);
    }

    [Fact]
    public async Task A_successful_dispatch_logs_exactly_one_structured_information_entry_with_every_Documento17_28_audit_field()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var @event = Event(tenantId, reservationId, correlationId);

        var dispatcher = new FakeWorkflowCommandDispatcher();
        var logger = new RecordingLogger();
        var orchestrator = Orchestrator(dispatcher, logger, new FixedTimeProvider(DateTimeOffset.UtcNow));

        await orchestrator.HandleAsync(@event, CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);
        entry.Exception.Should().BeNull();

        var state = entry.State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["WorkflowName"].Should().Be("Workflow02_GuestCheckedOut");
        state["Trigger"].Should().Be(nameof(GuestCheckedOut));
        state["ActorType"].Should().Be("System");
        state["TenantId"].Should().Be(tenantId);
        state["ReservationId"].Should().Be(reservationId);
        state["SourceEventId"].Should().Be(@event.EventId);
        state["CorrelationId"].Should().Be(correlationId);
        state["Action"].Should().Be(nameof(CloseReservation));
        state["Result"].Should().Be("CommandDispatched");
        state.Should().ContainKey("DurationMs");
    }

    [Fact]
    public async Task A_failed_dispatch_logs_exactly_one_structured_error_entry_then_rethrows_without_swallowing()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var @event = Event(tenantId, reservationId, correlationId);

        var failure = new InvalidOperationException("transport unavailable");
        var dispatcher = new FakeWorkflowCommandDispatcher { FailWith = failure };
        var logger = new RecordingLogger();
        var orchestrator = Orchestrator(dispatcher, logger, new FixedTimeProvider(DateTimeOffset.UtcNow));

        var act = async () => await orchestrator.HandleAsync(@event, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(failure);

        var state = entry.State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["WorkflowName"].Should().Be("Workflow02_GuestCheckedOut");
        state["Trigger"].Should().Be(nameof(GuestCheckedOut));
        state["ActorType"].Should().Be("System");
        state["TenantId"].Should().Be(tenantId);
        state["ReservationId"].Should().Be(reservationId);
        state["SourceEventId"].Should().Be(@event.EventId);
        state["CorrelationId"].Should().Be(correlationId);
        state["Action"].Should().Be(nameof(CloseReservation));
        state["Result"].Should().Be("CommandDispatchFailed");
        state.Should().ContainKey("DurationMs");

        dispatcher.DispatchedCommands.Should().BeEmpty();
    }
}
