using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Domain;

/// <summary>
/// A single append-only audit record for this Bounded Context's own
/// transactional audit trail (Checkpoint 0 plan, item 11) — deliberately
/// separate from <c>identity.security_audit_log</c>, never reused across
/// contexts. Written in the same transaction as the change it records;
/// nothing in this Bounded Context updates or deletes a row here (enforced
/// additionally at the database level: the application role has no
/// UPDATE/DELETE grant on <c>property_management.property_audit_log</c>).
///
/// <see cref="ChangedFields"/> carries only field <i>names</i>
/// (snake_case, e.g. <c>"name"</c>, <c>"capacity"</c>) — never old/new
/// values, addresses, or any other content (Checkpoint 0 plan, item 11).
/// </summary>
public sealed class PropertyAuditEntry : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string EntityType { get; private set; } = null!;
    public Guid AggregateId { get; private set; }
    public string ActionCode { get; private set; } = null!;
    public IReadOnlyList<string> ChangedFields { get; private set; } = Array.Empty<string>();
    public DateTimeOffset OccurredAt { get; private set; }

    private PropertyAuditEntry()
    {
        // EF Core materialization.
    }

    private PropertyAuditEntry(
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

    public static PropertyAuditEntry Create(
        Guid id, Guid tenantId, Guid actorUserId, string entityType, Guid aggregateId,
        string actionCode, IReadOnlyList<string> changedFields, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("Entity type cannot be empty.", nameof(entityType));

        if (string.IsNullOrWhiteSpace(actionCode))
            throw new ArgumentException("Action code cannot be empty.", nameof(actionCode));

        return new PropertyAuditEntry(
            id, tenantId, actorUserId, entityType, aggregateId, actionCode, changedFields, occurredAt);
    }
}
