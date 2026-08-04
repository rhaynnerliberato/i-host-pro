using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Identity &amp; Access Bounded Context's own DbContext, owning the
/// `identity` PostgreSQL schema (Architecture Principles, Section 10;
/// ADR-003). Inherits the mandatory tenant Global Query Filter from
/// <see cref="BaseDbContext"/> for every entity implementing
/// <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>; Row-Level
/// Security is applied at the database level as a second, independent layer
/// of defense (Incremento 1 plan, Section 5).
///
/// <see cref="ModelBuilder.MapWolverineEnvelopeStorage(ModelBuilder, string?)"/>
/// (Checkpoint 6 homologação, third production defect fix) maps this
/// context's model to the pre-existing <c>identity_messaging</c> envelope
/// tables (provisioned by <c>IHostPro.MigrationRunner</c> via Weasel, never
/// by EF migrations — <c>ExcludeFromMigrations()</c> inside Wolverine's own
/// mapping keeps them out of this context's migration history). Without
/// this call, <c>DbContext.IsWolverineEnabled()</c> is false and
/// <c>EfCoreEnvelopeTransaction.PersistOutgoingAsync</c> falls back to its
/// raw-ADO path, which resolves the target store via
/// <c>MessageContext.TryFindMessageDatabase</c> — that method only ever
/// returns <c>context.Storage</c> (the Main store) or a multi-tenant store,
/// with no awareness of the <c>identity_messaging</c> Ancillary store this
/// context is enrolled to in <c>Program.cs</c> — so every event ended up
/// durably persisted in <c>platform_messaging</c> instead. Confirmed by
/// reading Wolverine 6.22.0's actual source
/// (<c>EfCoreEnvelopeTransaction.cs</c>, <c>IMessageDatabase.cs</c>) at the
/// exact installed commit, not assumed.
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

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "identity_messaging", typeof(IdentityDbContext)) schema literal exactly.
        modelBuilder.MapWolverineEnvelopeStorage("identity_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
