using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a Cleaning transitions <c>Assigned</c> → <c>InTransit</c>
/// (Fase 7, Incremento 1 — Agenda Foundation, Checkpoint 1 closure — new
/// event, approved: "InTransit é estado real do aggregate... sem evento não
/// existe forma arquiteturalmente válida de manter a projeção correta").
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the Cleaning's id/<c>"Cleaning"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Housekeeper who reported it (self-service only —
/// <c>MarkOwnCleaningInTransitCommandHandler</c>). Deliberately minimal —
/// no <c>PropertyId</c>/<c>HousekeeperUserId</c>/description/actor beyond
/// the envelope: the one real consumer (Reservation &amp; Scheduling's
/// Agenda projection) only needs to identify the Cleaning and apply
/// <c>Status = InTransit</c> to a row it already owns.
/// </summary>
public sealed record CleaningInTransit : IntegrationEvent
{
    public required Guid CleaningId { get; init; }
}
