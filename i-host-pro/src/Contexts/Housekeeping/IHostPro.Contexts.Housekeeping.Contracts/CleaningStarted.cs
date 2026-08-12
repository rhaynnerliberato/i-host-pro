using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when cleaning work begins (Fase 6, Incremento 1) —
/// <c>Assigned</c> → <c>Started</c> (the optional <c>InTransit</c> step is
/// never required this increment — Documento 06's own note that it is
/// "Configuração por tenant"). <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the Cleaning's
/// id/<c>"Cleaning"</c>.
/// </summary>
public sealed record CleaningStarted : IntegrationEvent
{
    public required Guid CleaningId { get; init; }
}
