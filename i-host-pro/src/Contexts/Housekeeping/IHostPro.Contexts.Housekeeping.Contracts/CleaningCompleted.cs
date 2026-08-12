using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a Cleaning finishes inspection and the property becomes
/// ready (Fase 6, Incremento 1) — <c>InInspection</c> → <c>Completed</c>,
/// terminal. Documento 06 §5 names this as the trigger that "poderá
/// liberar" Early Check-in eligibility — no such consumer exists yet
/// (Configuration &amp; Policy's <c>EarlyCheckInPolicy.RequiresCleaningCompleted</c>
/// has no real reader of this event today; Fase 6 is its first potential
/// producer). <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the Cleaning's id/<c>"Cleaning"</c>.
/// </summary>
public sealed record CleaningCompleted : IntegrationEvent
{
    public required Guid CleaningId { get; init; }

    public required Guid PropertyId { get; init; }
}
