using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Authorization;

/// <inheritdoc cref="IIdentityUserEligibilityReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IIdentityUserEligibilityReader"/> (Checkpoint 5 plan, item 5) —
/// lives in <c>Identity.Infrastructure</c>, the one layer allowed to touch
/// <see cref="IdentityDbContext"/> directly. Opens its own short-lived,
/// read-only, tenant-scoped transaction via <see cref="TenantAwareTransactionScope"/>
/// using a throwaway local <see cref="TenantContext"/> set to the caller-supplied
/// <paramref name="tenantId"/> — deliberately independent of whatever
/// ambient <c>ITenantContext</c> this scope's <see cref="IdentityDbContext"/>
/// was constructed with (already resolved identically, from the same JWT
/// claim, by <c>ConfigureJwtBearerOptions.OnTokenValidatedAsync</c> before
/// any Command/Query dispatches — the ambient value already scopes this
/// IdentityDbContext's own Global Query Filter). This local scope exists purely to
/// satisfy PostgreSQL's OWN Row-Level Security enforcement
/// (<c>SET LOCAL app.tenant_id</c>), a second, independent layer that does
/// not consult EF Core's filter at all — mirroring exactly how
/// <c>PropertyManagementFoundationTests</c>/every other test fixture in this
/// codebase already opens a tenant-scoped read against a role it does not
/// own an ambient context for. No cache, no audit, no event, no mutation
/// (item 5).
/// </remarks>
public sealed class IdentityUserEligibilityReader : IIdentityUserEligibilityReader
{
    private readonly IdentityDbContext _dbContext;

    public IdentityUserEligibilityReader(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<IdentityUserEligibility?> GetAsync(
        Guid tenantId, Guid userId, string requiredRoleCode, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
            return null;

        var hasRequiredRole = await _dbContext.UserRoles
            .AsNoTracking()
            .AnyAsync(ur => ur.UserId == userId && ur.RoleCode == requiredRoleCode, cancellationToken);

        return new IdentityUserEligibility(user.Id, user.Status == UserStatus.Active, hasRequiredRole);
    }
}
