using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// A policy value at <see cref="PolicyScopeType.Global"/> scope — Fase 5,
/// Incremento 1 official decisions §4: "Valores GLOBAL: não devem ser
/// misturados na tabela tenant-aware protegida por RLS. Persistência
/// separada, sem TenantId." Carries no <c>TenantId</c> and is never mapped
/// under Row-Level Security. Read-only in the MVP — this Bounded Context
/// exposes no tenant-facing command to create, alter or remove a row here;
/// the table may remain empty until a default value is explicitly approved
/// for a future increment (official decision 2.2).
/// </summary>
public sealed class GlobalPolicyValue : Entity<Guid>
{
    public string PolicyCode { get; private set; } = null!;
    public string Value { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private GlobalPolicyValue()
    {
        // EF Core materialization.
    }

    private GlobalPolicyValue(Guid id, string policyCode, string value, DateTimeOffset createdAtUtc)
        : base(id)
    {
        PolicyCode = policyCode;
        Value = value;
        CreatedAtUtc = createdAtUtc;
    }

    public static GlobalPolicyValue Create(Guid id, string policyCode, string value, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(policyCode))
            throw new ArgumentException("Policy code cannot be empty.", nameof(policyCode));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Policy value cannot be empty.", nameof(value));

        return new GlobalPolicyValue(id, policyCode, value, createdAtUtc);
    }
}
