using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Domain;

/// <summary>
/// A single occurrence registered by the assigned housekeeper against their
/// own Cleaning (Fase 6, Incremento 2A) — Documento 12's "Intercorrência":
/// "Problema encontrado." Deliberately an immutable, append-only
/// <see cref="Entity{TId}"/>, never an <see cref="AggregateRoot{TId}"/> with
/// its own state machine (approval §15 — Documento 06 §8's own rich
/// Intercorrência state machine, and Workflow 11's "Anexar Fotos"/"Relacionar
/// Reserva"/"Notificar Administrador" steps, exceed what this increment
/// supports without Files/new integration events; <see cref="CleaningId"/>
/// already transitively links to any Reservation via <c>Cleaning.ReservationId</c>).
/// No evidence/photo reference exists here (Files explicitly out of scope —
/// approval §2). Nothing in this Bounded Context updates or deletes a row
/// here — same append-only convention as <see cref="CleaningAuditEntry"/>.
/// </summary>
public sealed class CleaningOccurrence : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid CleaningId { get; private set; }
    public OccurrenceType Type { get; private set; }
    public string? Description { get; private set; }
    public Guid RegisteredByUserId { get; private set; }
    public DateTimeOffset RegisteredAtUtc { get; private set; }

    private CleaningOccurrence()
    {
        // EF Core materialization.
    }

    private CleaningOccurrence(
        Guid id, Guid tenantId, Guid cleaningId, OccurrenceType type, string? description,
        Guid registeredByUserId, DateTimeOffset registeredAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        CleaningId = cleaningId;
        Type = type;
        Description = description;
        RegisteredByUserId = registeredByUserId;
        RegisteredAtUtc = registeredAtUtc;
    }

    public static CleaningOccurrence Create(
        Guid id, Guid tenantId, Guid cleaningId, OccurrenceType type, string? description,
        Guid registeredByUserId, DateTimeOffset registeredAtUtc) =>
        new(id, tenantId, cleaningId, type, description, registeredByUserId, registeredAtUtc.ToUniversalTime());
}
