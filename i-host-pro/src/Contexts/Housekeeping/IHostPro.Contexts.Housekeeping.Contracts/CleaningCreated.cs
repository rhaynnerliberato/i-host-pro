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
/// reference — never inferred. <see cref="ScheduledAtUtc"/> (Fase 7,
/// Incremento 1 — Agenda Foundation) mirrors <c>Cleaning.ScheduledAtUtc</c>
/// exactly — never recomputed from <see cref="IntegrationEvent.Timestamp"/>
/// or any other source. Deliberately nullable, not <c>required</c>: a
/// Cleaning can be created without a schedule (<c>CreateCleaningRequest</c>'s
/// own contract already allows this — "the cleaning simply does not
/// participate in schedule-based ordering until an administrator sets it").
/// A consumer MUST treat <c>null</c> as "no known schedule" — never default
/// it to <see cref="DateTimeOffset.MinValue"/> or any other placeholder
/// date. This also makes the field additive/backward-safe for any envelope
/// serialized before this field existed (deserializes to <c>null</c>, never
/// throws) — but that is a secondary consequence, not the reason for the
/// nullability, which mirrors genuine, current domain optionality.
/// </summary>
public sealed record CleaningCreated : IntegrationEvent
{
    public required Guid CleaningId { get; init; }

    public required Guid PropertyId { get; init; }

    public Guid? ReservationId { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ScheduledAtUtc { get; init; }
}
