using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a Cleaning transitions to <c>Cancelled</c> (Fase 6,
/// Incremento 1) — terminal, never on a rejected/already-cancelled
/// attempt. Fired either by an explicit administrative cancellation or by
/// the automatic reaction to the linked Reservation's own
/// <c>ReservationCancelled</c> (Checkpoint 3). <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the Cleaning's
/// id/<c>"Cleaning"</c>.
/// </summary>
public sealed record CleaningCancelled : IntegrationEvent
{
    public required Guid CleaningId { get; init; }
}
