using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Identity &amp; Access Bounded Context's own DbContext, owning the
/// `identity` PostgreSQL schema (Architecture Principles, Section 10;
/// ADR-003). Inherits the mandatory tenant Global Query Filter from
/// <see cref="BaseDbContext"/> for every entity implementing
/// <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>; Row-Level
/// Security is applied at the database level as a second, independent layer
/// of defense (Incremento 1 plan, Section 5).
/// </summary>
public sealed class IdentityDbContext : BaseDbContext
{
    public override string SchemaName => "identity";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SecurityAuditEntry> SecurityAuditLog => Set<SecurityAuditEntry>();

    public IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every entity in this context is materialized through its domain
        // methods, never through public setters (Incremento 1 plan, Section 5)
        // — PreferField lets EF Core use the compiler-generated backing fields
        // of auto-properties without requiring a public setter.
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
