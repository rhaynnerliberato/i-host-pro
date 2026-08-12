using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a new Cleaning is created (Fase 6, Incremento 1) — always
/// <c>Status = "Pending"</c>, since every Cleaning is born <c>Pending</c>
/// this increment (creation is administrative/manual — Checkpoint 0 gate,
/// no automatic derivation from a Reservation's checkout). <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the new Cleaning's
/// id/<c>"Cleaning"</c>. <see cref="IntegrationEvent.ActorId"/> is the
/// Administrator/Operator who created it. <see cref="ReservationId"/> is
/// <c>null</c> when the Cleaning was created without a Reservation
/// reference — never inferred.
/// </summary>
public sealed record CleaningCreated : IntegrationEvent
{
    public required Guid CleaningId { get; init; }

    public required Guid PropertyId { get; init; }

    public Guid? ReservationId { get; init; }

    public required string Status { get; init; }
}
