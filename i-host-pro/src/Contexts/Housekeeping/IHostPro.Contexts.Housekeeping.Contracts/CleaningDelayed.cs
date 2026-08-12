using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a housekeeper reports a delay for their own Cleaning
/// (Fase 6, Incremento 2A) — Documento 07 catalogues this event by name
/// only ("Faxina atrasada"), no field-level payload schema, so no field
/// beyond <see cref="CleaningId"/> is added (parsimony rule — approval
/// §12-14). Never changes <see cref="Domain.Cleaning.Status"/>; Documento 06
/// documents no dedicated "Atrasada" state. <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the Cleaning's
/// id/<c>"Cleaning"</c>. <see cref="IntegrationEvent.ActorId"/> is always the
/// assigned housekeeper — this event has no administrative equivalent.
/// </summary>
public sealed record CleaningDelayed : IntegrationEvent
{
    public required Guid CleaningId { get; init; }
}
