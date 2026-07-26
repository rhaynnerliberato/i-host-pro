namespace IHostPro.BuildingBlocks.Infrastructure.Multitenancy;

/// <summary>
/// Resolves the tenant of the current request/execution scope. Used by
/// <see cref="BaseDbContext"/> to apply a global query filter on every entity,
/// guaranteeing that no query can accidentally cross tenant boundaries
/// (Architecture Principles, Section 7).
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    bool IsResolved { get; }

    void SetTenant(Guid tenantId);
}
