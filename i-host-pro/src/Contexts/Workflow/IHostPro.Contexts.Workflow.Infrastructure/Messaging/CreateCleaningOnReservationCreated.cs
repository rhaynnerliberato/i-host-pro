using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Wolverine;

namespace IHostPro.Contexts.Workflow.Infrastructure.Messaging;

/// <summary>
/// The sole trigger→action reaction this Checkpoint implements (Fase 8,
/// Checkpoint 1 — ADR-018): a new Reservation always requests Housekeeping
/// create the corresponding Cleaning. A pure, stateless transformation —
/// reads only fields already present on <see cref="ReservationCreated"/>,
/// touches no persistence, and sends exactly one command
/// (<c>Send</c>, never <c>Publish</c> — <see cref="CreateCleaningForReservation"/>
/// has exactly one destination Bounded Context, per ADR-018).
/// <see cref="CreateCleaningForReservation.CorrelationId"/>/<see cref="CreateCleaningForReservation.CausationId"/>
/// carry the triggering event's own correlation/id forward, for end-to-end
/// tracing across the Workflow → Housekeeping hop.
/// </summary>
public sealed class CreateCleaningOnReservationCreated : IIntegrationEventHandler<ReservationCreated>
{
    private readonly IMessageBus _messageBus;

    public CreateCleaningOnReservationCreated(IMessageBus messageBus) => _messageBus = messageBus;

    public async Task HandleAsync(ReservationCreated @event, CancellationToken cancellationToken)
    {
        var command = new CreateCleaningForReservation
        {
            TenantId = @event.TenantId,
            ReservationId = @event.ReservationId,
            PropertyId = @event.PropertyId,
            CorrelationId = @event.CorrelationId,
            CausationId = @event.EventId,
        };

        await _messageBus.SendAsync(command);
    }
}
