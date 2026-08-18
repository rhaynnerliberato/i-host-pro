using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Workflow.Application;

namespace IHostPro.Contexts.Workflow.Tests.Unit;

/// <summary>
/// Fase 8, Checkpoint 1.1: proves the orchestration use case in isolation
/// from any transport (no Wolverine, no IMessageBus — that belongs to
/// <c>WolverineWorkflowCommandDispatcher</c>'s own, separate coverage in
/// <c>Workflow.Infrastructure</c>'s integration tests). A
/// <see cref="ReservationCreated"/> input must always translate into
/// EXACTLY one dispatched <see cref="CreateCleaningForReservation"/> command,
/// with the minimal, non-PII payload ADR-018 requires.
/// </summary>
public class ReservationCreatedCleaningOrchestratorTests
{
    private sealed class FakeWorkflowCommandDispatcher : IWorkflowCommandDispatcher
    {
        public List<CreateCleaningForReservation> DispatchedCommands { get; } = [];

        public Task DispatchCreateCleaningForReservationAsync(
            CreateCleaningForReservation command, CancellationToken cancellationToken)
        {
            DispatchedCommands.Add(command);
            return Task.CompletedTask;
        }
    }

    private static ReservationCreated Event(Guid tenantId, Guid reservationId, Guid propertyId, Guid correlationId, Guid eventId) => new()
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
    };

    [Fact]
    public async Task A_ReservationCreated_event_dispatches_exactly_one_command_with_the_translated_identifiers()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var @event = Event(tenantId, reservationId, propertyId, correlationId, Guid.NewGuid());

        var dispatcher = new FakeWorkflowCommandDispatcher();
        var orchestrator = new ReservationCreatedCleaningOrchestrator(dispatcher);

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
        var @event = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dispatcher = new FakeWorkflowCommandDispatcher();
        var orchestrator = new ReservationCreatedCleaningOrchestrator(dispatcher);

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
        var orchestrator = new ReservationCreatedCleaningOrchestrator(dispatcher);

        var first = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var second = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await orchestrator.HandleAsync(first, CancellationToken.None);
        await orchestrator.HandleAsync(second, CancellationToken.None);

        dispatcher.DispatchedCommands.Should().HaveCount(2);
        dispatcher.DispatchedCommands[0].ReservationId.Should().Be(first.ReservationId);
        dispatcher.DispatchedCommands[1].ReservationId.Should().Be(second.ReservationId);
    }
}
