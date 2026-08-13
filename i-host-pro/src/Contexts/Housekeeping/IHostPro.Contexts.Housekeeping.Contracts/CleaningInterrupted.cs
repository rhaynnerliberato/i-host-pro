using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a Cleaning transitions <c>Started</c> → <c>Interrupted</c>
/// (Fase 7, Incremento 1 — Agenda Foundation, Checkpoint 1 closure — new
/// event, approved for the same reason as <see cref="CleaningInTransit"/>).
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the Cleaning's id/<c>"Cleaning"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator/Operator who recorded it
/// (<c>MarkCleaningInterruptedCommandHandler</c>). Deliberately minimal — no
/// cause/reason: the one real consumer only needs to identify the Cleaning
/// and apply <c>Status = Interrupted</c> to a row it already owns.
/// </summary>
public sealed record CleaningInterrupted : IntegrationEvent
{
    public required Guid CleaningId { get; init; }
}
