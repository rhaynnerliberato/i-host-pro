using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IHostPro.TenantProvisioning;

/// <summary>
/// The one initial-admin role every fresh tenant needs to actually use the
/// system - matches the "ADMIN" code already seeded once (platform-wide,
/// not per-tenant) by the Identity module's own InitialCreate migration.
/// </summary>
public static class AdminRole
{
    public const string Code = "ADMIN";
}

public sealed record ProvisioningRequest(
    TenantSlug TenantSlug,
    string TenantName,
    string AdminEmail,
    string AdminFullName,
    string AdminPassword);

public sealed record ProvisioningResult(
    Guid TenantId,
    bool TenantCreated,
    Guid UserId,
    bool UserCreated,
    bool AdminRoleAssigned);

/// <summary>
/// CP5.3D-C corrective Decision Gate: the real, explicit-execution,
/// idempotent mechanism for provisioning a Tenant + initial Admin user in
/// Homolog/Production - <c>DevelopmentIdentitySeeder</c> exists only for
/// Development and is never used here (item 1 of that gate). Mirrors the
/// exact domain/persistence recipe already proven by
/// <c>DevelopmentIdentitySeeder</c> and
/// <c>WebE2EFixture.CreateAdditionalTenantWithAdminAsync</c> - real
/// <see cref="Tenant.Provision"/>/<see cref="User.Register"/>/<see cref="UserRole"/>,
/// real Argon2id hashing, real <see cref="SecurityAuditEntry"/> rows - never
/// raw INSERT SQL, so domain invariants and password policy are never
/// bypassed. Deliberately does NOT enqueue the <c>UserCreated</c>/
/// <c>UserRoleAssigned</c> Wolverine integration events: nothing in this
/// codebase subscribes to either today (confirmed by audit before writing
/// this), and standing up the full outbox/RabbitMQ topology inside a
/// one-off console tool for zero real consumers would be disproportionate -
/// flagged as a conscious, revisitable decision, not an oversight.
/// </summary>
public sealed class TenantProvisioner
{
    private readonly IdentityDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;

    public TenantProvisioner(IdentityDbContext dbContext, ITenantContext tenantContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _timeProvider = timeProvider;
    }

    public async Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // The whole operation - tenant creation included - runs inside ONE
        // transaction, exactly like DevelopmentIdentitySeeder: opening it
        // BEFORE the tenant lookup/insert (not after) is what guarantees a
        // later failure (e.g. password policy) rolls the tenant insert back
        // too, instead of leaving a dangling tenant with no admin. Caught by
        // this tool's own integration test before this was fixed - the
        // transaction must wrap everything, not just the RLS-protected part.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // The `tenants` table is deliberately NOT RLS-protected (Tenant is
        // the tenant boundary itself) - safe to query/insert before any
        // tenant context is resolved, exactly like DevelopmentIdentitySeeder.
        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Slug == request.TenantSlug, cancellationToken);
        var tenantCreated = tenant is null;
        if (tenant is null)
        {
            tenant = Tenant.Provision(Guid.NewGuid(), request.TenantSlug, request.TenantName, now);
            _dbContext.Tenants.Add(tenant);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Every RLS-protected table (users/user_roles/security_audit_log)
        // from here on requires BOTH the EF-side ITenantContext (drives the
        // Global Query Filter) AND the Postgres-side SET LOCAL app.tenant_id
        // (drives the RLS policy itself) - the two are independent
        // mechanisms, confirmed by this session's own architecture audit.
        _tenantContext.SetTenant(tenant.Id);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenant.Id.ToString()}, true)", cancellationToken);

        var email = Email.Create(request.AdminEmail);
        var user = await _dbContext.Users
            .SingleOrDefaultAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == email.NormalizedValue, cancellationToken);
        var userCreated = user is null;
        var adminRoleAssigned = false;

        if (user is null)
        {
            var passwordValidator = new PasswordPolicyValidator(Options.Create(new PasswordPolicyOptions()));
            var validation = await passwordValidator.ValidateAsync(manager: null!, user: null!, request.AdminPassword);
            if (!validation.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Generated admin password failed policy validation: {string.Join(", ", validation.Errors.Select(e => e.Code))}");
            }

            var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
            var passwordHash = PasswordHash.FromEncoded(hasher.HashPassword(null!, request.AdminPassword));

            user = User.Register(Guid.NewGuid(), tenant.Id, email, request.AdminFullName, passwordHash, now);
            _dbContext.Users.Add(user);

            var userRole = new UserRole(tenant.Id, user.Id, AdminRole.Code, now, assignedByUserId: null);
            _dbContext.UserRoles.Add(userRole);
            adminRoleAssigned = true;

            var correlationId = Guid.NewGuid();
            _dbContext.SecurityAuditLog.Add(SecurityAuditEntry.Record(
                Guid.NewGuid(), tenant.Id, SecurityAuditEventType.UserCreated, now, correlationId,
                reasonCode: null, userId: user.Id, actorId: null, sessionId: null, refreshTokenId: null, ipAddress: null));
            _dbContext.SecurityAuditLog.Add(SecurityAuditEntry.Record(
                Guid.NewGuid(), tenant.Id, SecurityAuditEventType.UserRoleAssigned, now, correlationId,
                reasonCode: null, userId: user.Id, actorId: null, sessionId: null, refreshTokenId: null, ipAddress: null));

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // Reconcile only: an existing admin's password is never touched
            // here (CP5.3D-C corrective Decision Gate item 4/9) - only a
            // missing ADMIN role is safe to add back.
            var hasAdminRole = await _dbContext.UserRoles
                .AnyAsync(r => r.TenantId == tenant.Id && r.UserId == user.Id && r.RoleCode == AdminRole.Code, cancellationToken);
            if (!hasAdminRole)
            {
                _dbContext.UserRoles.Add(new UserRole(tenant.Id, user.Id, AdminRole.Code, now, assignedByUserId: null));
                adminRoleAssigned = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new ProvisioningResult(tenant.Id, tenantCreated, user.Id, userCreated, adminRoleAssigned);
    }
}
