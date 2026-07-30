using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="ILastAdministratorGuard"/>
/// <remarks>
/// Mirrors <c>DevelopmentIdentitySeeder</c>'s <c>pg_advisory_xact_lock(hashtext(...))</c>
/// pattern (see its own doc comment) — auto-released at COMMIT/ROLLBACK, so a
/// crashed process can never leave the lock held. Scoped per-TENANT here
/// (rather than the seeder's single fixed key), so two different tenants'
/// removals never contend with each other, while two concurrent removals
/// within the SAME tenant serialize on it: the second call blocks on
/// <c>pg_advisory_xact_lock</c> until the first transaction commits or rolls
/// back, then re-reads the now-current state below — never two concurrent
/// removals both observing "another Administrator still exists" for the same
/// pair (Incremento 3, Checkpoint 6, Section 5).
///
/// The lock must run on the SAME <see cref="IdentityDbContext"/>/connection
/// the rest of the transaction uses — raw SQL issued through EF Core always
/// executes on the context's current connection/transaction, so this
/// naturally joins whatever <c>TenantAwareTransactionScope</c> already
/// opened; no separate connection or explicit transaction handling is
/// needed here.
/// </remarks>
public sealed class LastAdministratorGuard : ILastAdministratorGuard
{
    private const string AdministratorRoleCode = "ADMIN";

    private readonly IdentityDbContext _dbContext;

    public LastAdministratorGuard(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<bool> AnotherActiveAdministratorRemainsAsync(
        Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var lockKey = $"ihostpro:identity:admin-guard:{tenantId}";
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", cancellationToken);

        // Excluding userId already represents the post-removal state for the
        // ADMIN role specifically — the only thing this removal changes.
        return await _dbContext.Users
            .Where(u => u.Id != userId && u.Status == UserStatus.Active)
            .Join(_dbContext.UserRoles, u => u.Id, ur => ur.UserId, (u, ur) => ur.RoleCode)
            .AnyAsync(roleCode => roleCode == AdministratorRoleCode, cancellationToken);
    }
}
