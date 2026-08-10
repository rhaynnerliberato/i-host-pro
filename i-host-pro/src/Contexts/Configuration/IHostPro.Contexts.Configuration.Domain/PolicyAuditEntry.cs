using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// A single append-only audit record for this Bounded Context's own
/// transactional audit trail (Fase 5, Incremento 1 official decisions §4) —
/// written in the same transaction as the <see cref="PolicyValue"/> version
/// it records. Nothing in this Bounded Context updates or deletes a row
/// here (enforced additionally at the database level: the application role
/// has no UPDATE/DELETE grant on <c>configuration.policy_audit_log</c>, same
/// convention as <c>reservations.reservation_audit_log</c>).
///
/// Carries the previous and new <see cref="Value"/> of the policy itself
/// (unlike Reservations' own audit log, which stores only changed field
/// names) — Fase 5, Incremento 1 official decisions §4 requires the actual
/// values, never just field names, for this context. Never records
/// passwords, tokens or credentials — no field here can hold them.
/// </summary>
public sealed class PolicyAuditEntry : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string PolicyCode { get; private set; } = null!;
    public PolicyScopeType ScopeType { get; private set; }
    public Guid? ScopeReferenceId { get; private set; }
    public int? PreviousVersion { get; private set; }
    public int NewVersion { get; private set; }
    public string? PreviousValue { get; private set; }
    public string NewValue { get; private set; } = null!;
    public Guid AuthorUserId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string Reason { get; private set; } = null!;
    public string Origin { get; private set; } = null!;
    public string? SessionId { get; private set; }
    public string? IpAddress { get; private set; }

    private PolicyAuditEntry()
    {
        // EF Core materialization.
    }

    private PolicyAuditEntry(
        Guid id, Guid tenantId, string policyCode, PolicyScope scope,
        int? previousVersion, int newVersion, string? previousValue, string newValue,
        Guid authorUserId, DateTimeOffset occurredAtUtc, string reason, string origin,
        string? sessionId, string? ipAddress)
        : base(id)
    {
        TenantId = tenantId;
        PolicyCode = policyCode;
        ScopeType = scope.Type;
        ScopeReferenceId = scope.ReferenceId;
        PreviousVersion = previousVersion;
        NewVersion = newVersion;
        PreviousValue = previousValue;
        NewValue = newValue;
        AuthorUserId = authorUserId;
        OccurredAtUtc = occurredAtUtc;
        Reason = reason;
        Origin = origin;
        SessionId = sessionId;
        IpAddress = ipAddress;
    }

    public static PolicyAuditEntry Create(
        Guid id, Guid tenantId, string policyCode, PolicyScope scope,
        int? previousVersion, int newVersion, string? previousValue, string newValue,
        Guid authorUserId, DateTimeOffset occurredAtUtc, string reason, string origin,
        string? sessionId = null, string? ipAddress = null)
    {
        if (string.IsNullOrWhiteSpace(policyCode))
            throw new ArgumentException("Policy code cannot be empty.", nameof(policyCode));
        if (scope.Type == PolicyScopeType.Global)
            throw new ArgumentException("PolicyAuditEntry does not support Global scope.", nameof(scope));
        if (newVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(newVersion), newVersion, "New version must be at least 1.");
        if (previousVersion is < 1)
            throw new ArgumentOutOfRangeException(nameof(previousVersion), previousVersion, "Previous version, when present, must be at least 1.");
        if (string.IsNullOrWhiteSpace(newValue))
            throw new ArgumentException("New value cannot be empty.", nameof(newValue));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason cannot be empty.", nameof(reason));
        if (string.IsNullOrWhiteSpace(origin))
            throw new ArgumentException("Origin cannot be empty.", nameof(origin));

        return new PolicyAuditEntry(
            id, tenantId, policyCode, scope, previousVersion, newVersion, previousValue, newValue,
            authorUserId, occurredAtUtc, reason, origin, sessionId, ipAddress);
    }
}
