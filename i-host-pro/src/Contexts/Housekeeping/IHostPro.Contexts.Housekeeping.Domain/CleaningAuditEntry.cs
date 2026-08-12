using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Housekeeping.Domain;

/// <summary>
/// A single append-only audit record for this Bounded Context's own
/// transactional audit trail (Fase 6, Incremento 1 plan) — mirrors
/// <c>Reservations.Domain.ReservationAuditEntry</c> exactly, deliberately
/// separate from Identity's/Property Management's/Reservations' own audit
/// logs. Written in the same transaction as the change it records; nothing
/// in this Bounded Context updates or deletes a row here (enforced
/// additionally at the database level: the application role has no
/// UPDATE/DELETE grant on <c>housekeeping.cleaning_audit_log</c>).
///
/// <see cref="ChangedFields"/> carries only field <i>names</i> — always
/// <c>["status"]</c> for a lifecycle transition, since <see cref="ActionCode"/>
/// already identifies which action occurred; never the old/new status
/// values themselves, mirroring Reservations' own data-minimization
/// convention.
/// </summary>
public sealed class CleaningAuditEntry : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string EntityType { get; private set; } = null!;
    public Guid AggregateId { get; private set; }
    public string ActionCode { get; private set; } = null!;
    public IReadOnlyList<string> ChangedFields { get; private set; } = Array.Empty<string>();
    public DateTimeOffset OccurredAt { get; private set; }

    private CleaningAuditEntry()
    {
        // EF Core materialization.
    }

    private CleaningAuditEntry(
        Guid id, Guid tenantId, Guid actorUserId, string entityType, Guid aggregateId,
        string actionCode, IReadOnlyList<string> changedFields, DateTimeOffset occurredAt)
        : base(id)
    {
        TenantId = tenantId;
        ActorUserId = actorUserId;
        EntityType = entityType;
        AggregateId = aggregateId;
        ActionCode = actionCode;
        ChangedFields = changedFields;
        OccurredAt = occurredAt;
    }

    public static CleaningAuditEntry Create(
        Guid id, Guid tenantId, Guid actorUserId, string entityType, Guid aggregateId,
        string actionCode, IReadOnlyList<string> changedFields, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("Entity type cannot be empty.", nameof(entityType));

        if (string.IsNullOrWhiteSpace(actionCode))
            throw new ArgumentException("Action code cannot be empty.", nameof(actionCode));

        return new CleaningAuditEntry(
            id, tenantId, actorUserId, entityType, aggregateId, actionCode, changedFields, occurredAt);
    }
}
