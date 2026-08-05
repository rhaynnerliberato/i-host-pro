using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Reservations.Domain;

/// <summary>
/// A single append-only audit record for this Bounded Context's own
/// transactional audit trail (Fase 3, Incremento 1 plan, item 11) —
/// deliberately separate from Identity's/Property Management's own audit
/// logs, never reused across contexts. Written in the same transaction as
/// the change it records; nothing in this Bounded Context updates or
/// deletes a row here (enforced additionally at the database level: the
/// application role has no UPDATE/DELETE grant on
/// <c>reservations.reservation_audit_log</c>).
///
/// <see cref="ChangedFields"/> carries only field <i>names</i> (snake_case,
/// e.g. <c>"guest_name"</c>, <c>"check_in_at"</c>) — never guest name/phone,
/// dates, guest counts, or any other content (Fase 3, Incremento 1 plan,
/// item 11: "não armazenar na auditoria" nome/telefone/datas/quantidade).
/// </summary>
public sealed class ReservationAuditEntry : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string EntityType { get; private set; } = null!;
    public Guid AggregateId { get; private set; }
    public string ActionCode { get; private set; } = null!;
    public IReadOnlyList<string> ChangedFields { get; private set; } = Array.Empty<string>();
    public DateTimeOffset OccurredAt { get; private set; }

    private ReservationAuditEntry()
    {
        // EF Core materialization.
    }

    private ReservationAuditEntry(
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

    public static ReservationAuditEntry Create(
        Guid id, Guid tenantId, Guid actorUserId, string entityType, Guid aggregateId,
        string actionCode, IReadOnlyList<string> changedFields, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("Entity type cannot be empty.", nameof(entityType));

        if (string.IsNullOrWhiteSpace(actionCode))
            throw new ArgumentException("Action code cannot be empty.", nameof(actionCode));

        return new ReservationAuditEntry(
            id, tenantId, actorUserId, entityType, aggregateId, actionCode, changedFields, occurredAt);
    }
}
