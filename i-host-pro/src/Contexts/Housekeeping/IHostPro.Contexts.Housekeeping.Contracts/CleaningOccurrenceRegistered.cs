using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// Published when a housekeeper registers a <c>CleaningOccurrence</c> against
/// their own Cleaning (Fase 7, Incremento 2 — Dashboard &amp; Reporting
/// Foundation, Checkpoint 0/1 decision). <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the new occurrence's
/// id/<c>"CleaningOccurrence"</c>. <see cref="IntegrationEvent.ActorId"/> is
/// the housekeeper who registered it.
///
/// Deliberately excludes <c>Description</c> (free-text, may contain
/// PII/business-sensitive content), <c>RegisteredByUserName</c> (Dashboard
/// stores no user display data — Checkpoint 0 decision 17), and any
/// evidence/photo reference (Files remains out of scope). <see cref="PropertyId"/>
/// is also deliberately omitted — Dashboard resolves it through
/// <see cref="CleaningId"/> against its own local Cleaning projection,
/// never duplicated here.
///
/// Dashboard & Reporting is this event's initial (and, this increment, only)
/// consumer — a Bounded Context whose classification (Architecture
/// Principles §3/§14) is Supporting/terminal, consuming events, never
/// commanding Housekeeping.
/// </summary>
public sealed record CleaningOccurrenceRegistered : IntegrationEvent
{
    public required Guid OccurrenceId { get; init; }

    public required Guid CleaningId { get; init; }

    public required string OccurrenceType { get; init; }

    public required DateTimeOffset RegisteredAtUtc { get; init; }
}
