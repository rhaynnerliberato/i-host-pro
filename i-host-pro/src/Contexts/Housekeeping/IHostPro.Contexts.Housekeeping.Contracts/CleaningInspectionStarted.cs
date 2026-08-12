using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when inspection begins after the cleaning work itself is
/// finished (Fase 6, Incremento 1) — <c>Started</c> → <c>InInspection</c>.
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the Cleaning's id/<c>"Cleaning"</c>.
/// </summary>
public sealed record CleaningInspectionStarted : IntegrationEvent
{
    public required Guid CleaningId { get; init; }
}
