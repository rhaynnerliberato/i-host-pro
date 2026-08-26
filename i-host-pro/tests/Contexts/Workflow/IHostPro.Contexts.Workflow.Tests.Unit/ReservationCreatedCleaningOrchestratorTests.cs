using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Workflow.Application;
using IHostPro.Contexts.Workflow.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Workflow.Tests.Unit;

/// <summary>
/// Fase 8, Checkpoint 1.1: proves the orchestration use case in isolation
/// from any transport (no Wolverine, no IMessageBus — that belongs to
/// <c>WolverineWorkflowCommandDispatcher</c>'s own, separate coverage in
/// <c>Workflow.Infrastructure</c>'s integration tests). A
/// <see cref="ReservationCreated"/> input must always translate into
/// EXACTLY one dispatched <see cref="CreateCleaningForReservation"/> command,
/// with the minimal, non-PII payload ADR-018 requires.
///
/// Fase 8, Checkpoint 2.1 (corrective audit gate): also proves the
/// structured, PII-safe audit log entry Documento 17 §28 requires — on both
/// the success and failure paths — via a hand-rolled <c>RecordingLogger</c>
/// that captures the structured (key/value) log state, mirroring
/// <c>RefreshTokenTenantBootstrapResolverTests.RecordingLogger</c>'s
/// established pattern. Assertions read the structured state directly
/// rather than parsing the formatted message string, per the mandate's
/// preference for structured-state assertions over fragile string matching.
/// </summary>
public class ReservationCreatedCleaningOrchestratorTests
{
    private sealed class FakeWorkflowCommandDispatcher : IWorkflowCommandDispatcher
    {
        public List<CreateCleaningForReservation> DispatchedCommands { get; } = [];

        /// <summary>When set, the dispatch throws this instead of recording the command — simulates a transport failure without a real, destructive RabbitMQ outage.</summary>
        public Exception? FailWith { get; set; }

        public Task DispatchCreateCleaningForReservationAsync(
            CreateCleaningForReservation command, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
                throw FailWith;

            DispatchedCommands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<ReservationCreatedCleaningOrchestrator>
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

    private static ReservationCreated Event(Guid tenantId, Guid reservationId, Guid propertyId, Guid correlationId) => new()
    {
        TenantId = tenantId,
        AggregateId = reservationId,
        AggregateType = "Reservation",
        CorrelationId = correlationId,
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        ReservationId = reservationId,
        PropertyId = propertyId,
        Status = "confirmed",
        Source = "manual",
    };

    private static ReservationCreatedCleaningOrchestrator Orchestrator(
        IWorkflowCommandDispatcher dispatcher,
        ILogger<ReservationCreatedCleaningOrchestrator>? logger = null,
        TimeProvider? timeProvider = null) =>
        new(dispatcher, timeProvider ?? TimeProvider.System, logger ?? NullLogger<ReservationCreatedCleaningOrchestrator>.Instance);

    [Fact]
    public async Task A_ReservationCreated_event_dispatches_exactly_one_command_with_the_translated_identifiers()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var @event = Event(tenantId, reservationId, propertyId, correlationId);

        var dispatcher = new FakeWorkflowCommandDispatcher();
        var orchestrator = Orchestrator(dispatcher);

        await orchestrator.HandleAsync(@event, CancellationToken.None);

        dispatcher.DispatchedCommands.Should().ContainSingle();
        var command = dispatcher.DispatchedCommands[0];
        command.TenantId.Should().Be(tenantId);
        command.ReservationId.Should().Be(reservationId);
        command.PropertyId.Should().Be(propertyId);
        command.CorrelationId.Should().Be(correlationId);
        command.CausationId.Should().Be(@event.EventId);
    }

    [Fact]
    public async Task The_dispatched_command_carries_no_PII_and_no_invented_ScheduledAtUtc()
    {
        var @event = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dispatcher = new FakeWorkflowCommandDispatcher();
        var orchestrator = Orchestrator(dispatcher);

        await orchestrator.HandleAsync(@event, CancellationToken.None);

        // Structural proof by construction: CreateCleaningForReservation has
        // exactly five properties (TenantId/ReservationId/PropertyId/
        // CorrelationId/CausationId) — no guest name/phone, no
        // ScheduledAtUtc/CheckOutAt-derived field ever existed on this
        // contract for the orchestrator to populate.
        var properties = typeof(CreateCleaningForReservation).GetProperties();
        properties.Select(p => p.Name).Should().BeEquivalentTo(
            "TenantId", "ReservationId", "PropertyId", "CorrelationId", "CausationId");
    }

    [Fact]
    public async Task Two_different_ReservationCreated_events_each_dispatch_their_own_independent_command()
    {
        var dispatcher = new FakeWorkflowCommandDispatcher();
        var orchestrator = Orchestrator(dispatcher);

        var first = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var second = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

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
        var propertyId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var @event = Event(tenantId, reservationId, propertyId, correlationId);

        var dispatcher = new FakeWorkflowCommandDispatcher();
        var logger = new RecordingLogger();
        var orchestrator = Orchestrator(dispatcher, logger, new FixedTimeProvider(DateTimeOffset.UtcNow));

        await orchestrator.HandleAsync(@event, CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);
        entry.Exception.Should().BeNull();

        var state = entry.State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["WorkflowName"].Should().Be("Workflow01_NewReservation");
        state["Trigger"].Should().Be(nameof(ReservationCreated));
        state["ActorType"].Should().Be("System");
        state["TenantId"].Should().Be(tenantId);
        state["ReservationId"].Should().Be(reservationId);
        state["SourceEventId"].Should().Be(@event.EventId);
        state["CorrelationId"].Should().Be(correlationId);
        state["Action"].Should().Be(nameof(CreateCleaningForReservation));
        state["Result"].Should().Be("CommandDispatched");
        state.Should().ContainKey("DurationMs");
    }

    [Fact]
    public async Task A_failed_dispatch_logs_exactly_one_structured_error_entry_then_rethrows_without_swallowing()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var @event = Event(tenantId, reservationId, propertyId, correlationId);

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
        state["WorkflowName"].Should().Be("Workflow01_NewReservation");
        state["Trigger"].Should().Be(nameof(ReservationCreated));
        state["ActorType"].Should().Be("System");
        state["TenantId"].Should().Be(tenantId);
        state["ReservationId"].Should().Be(reservationId);
        state["SourceEventId"].Should().Be(@event.EventId);
        state["CorrelationId"].Should().Be(correlationId);
        state["Action"].Should().Be(nameof(CreateCleaningForReservation));
        state["Result"].Should().Be("CommandDispatchFailed");
        state.Should().ContainKey("DurationMs");

        // Never swallowed: nothing else observes or reports the failure —
        // the exception propagating out of HandleAsync (asserted above) IS
        // Workflow's only failure signal to its Wolverine caller.
        dispatcher.DispatchedCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task Neither_the_success_nor_the_failure_audit_entry_ever_carries_a_key_outside_the_approved_non_PII_vocabulary()
    {
        // No PII field (guest name/phone/address) is ever an input to this
        // orchestrator — ReservationCreated itself carries none (Fase 3's
        // own PII-absence tests already establish this at the source). This
        // guards the audit log's OWN field set against a future addition
        // silently introducing PII, independent of what the event carries.
        string[] allowedKeys =
        [
            "WorkflowName", "Trigger", "ActorType", "TenantId", "ReservationId",
            "SourceEventId", "CorrelationId", "Action", "Result", "DurationMs",
            "{OriginalFormat}",
        ];

        var successLogger = new RecordingLogger();
        await Orchestrator(new FakeWorkflowCommandDispatcher(), successLogger)
            .HandleAsync(Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        var failureLogger = new RecordingLogger();
        var failingDispatcher = new FakeWorkflowCommandDispatcher { FailWith = new InvalidOperationException("boom") };
        await FluentActions.Awaiting(() => Orchestrator(failingDispatcher, failureLogger)
                .HandleAsync(Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        successLogger.Entries.Should().ContainSingle();
        failureLogger.Entries.Should().ContainSingle();
        successLogger.Entries[0].State.Select(kvp => kvp.Key).Should().BeSubsetOf(allowedKeys);
        failureLogger.Entries[0].State.Select(kvp => kvp.Key).Should().BeSubsetOf(allowedKeys);
    }
}
