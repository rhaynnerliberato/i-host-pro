using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Domain;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <inheritdoc cref="IPolicyValueResolver"/>
/// <remarks>
/// Opens its own short-lived, read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/> using a throwaway local
/// <see cref="TenantContext"/> set to the caller-supplied
/// <paramref name="tenantId"/> (mirrors
/// <c>PropertyReservationEligibilityReader</c>'s own reasoning exactly) —
/// this scope exists purely to satisfy PostgreSQL's own Row-Level Security
/// enforcement for <c>policy_values</c>, and is always closed before this
/// method returns, so no Configuration transaction ever remains open
/// simultaneously with a consumer's own write transaction (Fase 5,
/// Incremento 1 official decisions §5). <c>global_policy_values</c> carries
/// no Row-Level Security and needs no tenant session variable, but is safely
/// queried inside the same short-lived transaction/connection regardless.
/// No audit, no mutation. This class never touches the cache itself
/// (Checkpoint 6) — <see cref="CachedPolicyValueResolver"/> wraps it and is
/// what every consumer actually resolves as <see cref="IPolicyValueResolver"/>.
/// </remarks>
internal sealed class PolicyValueResolver : IPolicyValueResolver
{
    private readonly ConfigurationDbContext _dbContext;

    public PolicyValueResolver(ConfigurationDbContext dbContext) => _dbContext = dbContext;

    public async Task<PolicyValueResolution> ResolveAsync(
        Guid tenantId, string policyCode, Guid? propertyId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        if (propertyId is not null)
        {
            var propertyValue = await _dbContext.PolicyValues.AsNoTracking()
                .Where(v =>
                    v.PolicyCode == policyCode &&
                    v.ScopeType == PolicyScopeType.Property &&
                    v.ScopeReferenceId == propertyId &&
                    v.IsCurrent)
                .FirstOrDefaultAsync(cancellationToken);

            if (propertyValue is not null)
                return new PolicyValueResolution(true, propertyValue.Value, ResolvedScopeKind.Property, propertyValue.Version);
        }

        var tenantValue = await _dbContext.PolicyValues.AsNoTracking()
            .Where(v => v.PolicyCode == policyCode && v.ScopeType == PolicyScopeType.Tenant && v.IsCurrent)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantValue is not null)
            return new PolicyValueResolution(true, tenantValue.Value, ResolvedScopeKind.Tenant, tenantValue.Version);

        var globalValue = await _dbContext.GlobalPolicyValues.AsNoTracking()
            .Where(v => v.PolicyCode == policyCode)
            .FirstOrDefaultAsync(cancellationToken);

        if (globalValue is not null)
            return new PolicyValueResolution(true, globalValue.Value, ResolvedScopeKind.Global, null);

        return new PolicyValueResolution(false, null, null, null);
    }
}
