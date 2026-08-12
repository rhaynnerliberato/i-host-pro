using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a Cleaning transitions to <c>WaitingMaterials</c> (Fase 6,
/// Incremento 2A) — <c>Started</c> → <c>WaitingMaterials</c>, triggered
/// either by the assigned housekeeper (self-service) or administratively.
/// Documento 07 catalogues this event by name only ("Solicitou materiais"),
/// no field-level payload schema, so no field beyond <see cref="CleaningId"/>
/// is added (parsimony rule — approval §12-14). <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the Cleaning's
/// id/<c>"Cleaning"</c>.
/// </summary>
public sealed record CleaningNeedsMaterial : IntegrationEvent
{
    public required Guid CleaningId { get; init; }
}
