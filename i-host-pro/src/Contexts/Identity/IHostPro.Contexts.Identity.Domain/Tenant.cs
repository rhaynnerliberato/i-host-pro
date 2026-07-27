using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// A tenant of the platform. Not <see cref="ITenantOwned"/> — it is the tenant
/// boundary itself, never Row-Level-Security-restricted (Incremento 1 plan,
/// "Tenant e RLS"). Provisioned only administratively/by seed in this phase —
/// no self-service signup, no plan/billing/subscription data
/// (Incremento 1 plan, decision 1).
/// </summary>
public sealed class Tenant : AggregateRoot<Guid>
{
    public TenantSlug Slug { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public TenantStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ActivatedAt { get; private set; }

    private Tenant()
    {
        // EF Core materialization.
    }

    private Tenant(Guid id, TenantSlug slug, string name, DateTimeOffset now) : base(id)
    {
        Slug = slug;
        Name = name;
        Status = TenantStatus.Active;
        CreatedAt = now;
        ActivatedAt = now;
    }

    public static Tenant Provision(Guid id, TenantSlug slug, string name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty.", nameof(name));

        return new Tenant(id, slug, name.Trim(), now);
    }

    public void Suspend() => Status = TenantStatus.Suspended;

    public void Reactivate() => Status = TenantStatus.Active;
}
