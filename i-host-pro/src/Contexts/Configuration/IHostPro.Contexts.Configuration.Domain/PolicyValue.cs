using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// A single, immutable, tenant-owned version of a policy's value at a given
/// <see cref="PolicyScopeType.Tenant"/> or <see cref="PolicyScopeType.Property"/>
/// scope — never <see cref="PolicyScopeType.Global"/> (Fase 5, Incremento 1
/// official decision 4/§4: GLOBAL values live in a separate, non-tenant-owned
/// table, see <c>GlobalPolicyValue</c>). Append-only: creating a new version
/// never mutates or removes a previous row; <see cref="Supersede"/> only
/// flips <see cref="IsCurrent"/> to <c>false</c> on the row a new version
/// replaces, in the same transaction as the new row's insert.
/// <see cref="Value"/> is the policy's raw JSON payload (validated against
/// its <see cref="PolicyDefinition"/>'s catalog shape by the Application
/// layer — Checkpoint 4 — never by this entity itself).
/// </summary>
public sealed class PolicyValue : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string PolicyCode { get; private set; } = null!;
    public PolicyScopeType ScopeType { get; private set; }
    public Guid? ScopeReferenceId { get; private set; }
    public int Version { get; private set; }
    public string Value { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public string Reason { get; private set; } = null!;
    public bool IsCurrent { get; private set; }

    private PolicyValue()
    {
        // EF Core materialization.
    }

    private PolicyValue(
        Guid id, Guid tenantId, string policyCode, PolicyScope scope, PolicyVersion version,
        string value, DateTimeOffset createdAtUtc, Guid createdByUserId, string reason, bool isCurrent)
        : base(id)
    {
        TenantId = tenantId;
        PolicyCode = policyCode;
        ScopeType = scope.Type;
        ScopeReferenceId = scope.ReferenceId;
        Version = version.Value;
        Value = value;
        CreatedAtUtc = createdAtUtc;
        CreatedByUserId = createdByUserId;
        Reason = reason;
        IsCurrent = isCurrent;
    }

    public static PolicyValue CreateInitialVersion(
        Guid id, Guid tenantId, string policyCode, PolicyScope scope,
        string value, DateTimeOffset createdAtUtc, Guid createdByUserId, string reason)
    {
        ValidateCommon(policyCode, scope, value, reason);

        return new PolicyValue(
            id, tenantId, policyCode, scope, PolicyVersion.First(), value, createdAtUtc, createdByUserId, reason, isCurrent: true);
    }

    public static PolicyValue CreateNextVersion(
        Guid id, Guid tenantId, string policyCode, PolicyScope scope, PolicyVersion previousVersion,
        string value, DateTimeOffset createdAtUtc, Guid createdByUserId, string reason)
    {
        ValidateCommon(policyCode, scope, value, reason);

        return new PolicyValue(
            id, tenantId, policyCode, scope, previousVersion.Next(), value, createdAtUtc, createdByUserId, reason, isCurrent: true);
    }

    /// <summary>
    /// Marks this row as no longer the current version — called on the
    /// previous current row, in the same transaction that inserts the new
    /// one. Never deletes or overwrites <see cref="Value"/>.
    /// </summary>
    public void Supersede() => IsCurrent = false;

    private static void ValidateCommon(string policyCode, PolicyScope scope, string value, string reason)
    {
        if (string.IsNullOrWhiteSpace(policyCode))
            throw new ArgumentException("Policy code cannot be empty.", nameof(policyCode));
        if (scope.Type == PolicyScopeType.Global)
            throw new ArgumentException("PolicyValue does not support Global scope — use GlobalPolicyValue instead.", nameof(scope));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Policy value cannot be empty.", nameof(value));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is mandatory when creating a new policy value version.", nameof(reason));
    }
}
