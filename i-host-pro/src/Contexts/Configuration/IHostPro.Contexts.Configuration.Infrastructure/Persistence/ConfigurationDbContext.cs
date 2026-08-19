using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence;

/// <summary>
/// The Configuration &amp; Policy Bounded Context's own DbContext, owning the
/// <c>configuration</c> PostgreSQL schema. Inherits the mandatory tenant
/// Global Query Filter from <see cref="BaseDbContext"/> for every entity
/// implementing <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>
/// (<see cref="PolicyValue"/>, <see cref="PolicyAuditEntry"/>);
/// Row-Level Security is applied at the database level as a second,
/// independent layer of defense — mirrors the same pattern already used by
/// every other Bounded Context's own DbContext exactly.
/// <see cref="PolicyDefinition"/> (platform catalog) and
/// <see cref="GlobalPolicyValue"/> (Fase 5 official decisions §4: GLOBAL
/// values must not be mixed into the tenant-aware RLS-protected table) carry
/// no <c>TenantId</c> and are never subject to the query filter or RLS.
///
/// <see cref="ModelBuilder.MapWolverineEnvelopeStorage(ModelBuilder, string?)"/>
/// maps this context's model to the <c>configuration_messaging</c> envelope
/// tables from Checkpoint 1 on — applying the Fase 2, Checkpoint 6 Wolverine
/// composition fix from day one, never reproducing the "envelopes silently
/// persisted to the Main Store" defect it corrected, even though no event is
/// published until Checkpoint 6.
/// </summary>
public sealed class ConfigurationDbContext : BaseDbContext
{
    public override string SchemaName => "configuration";

    public DbSet<PolicyDefinition> PolicyDefinitions => Set<PolicyDefinition>();
    public DbSet<PolicyValue> PolicyValues => Set<PolicyValue>();
    public DbSet<PolicyAuditEntry> PolicyAuditLog => Set<PolicyAuditEntry>();
    public DbSet<GlobalPolicyValue> GlobalPolicyValues => Set<GlobalPolicyValue>();

    /// <summary>Fase 9, Checkpoint 1 — "Comunicação e Integrações do MVP".</summary>
    public DbSet<Template> Templates => Set<Template>();

    public ConfigurationDbContext(DbContextOptions<ConfigurationDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every entity in this context will be materialized through its
        // domain methods, never through public setters — mirrors the same
        // convention used by every other Bounded Context's own DbContext
        // (Reservations, Property Management, Identity).
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ConfigurationDbContext).Assembly);

        // Must match Program.cs's/IHostPro.MigrationRunner's own
        // EnrollAncillaryPostgresqlOutbox(..., "configuration_messaging",
        // typeof(ConfigurationDbContext)) schema literal exactly.
        modelBuilder.MapWolverineEnvelopeStorage("configuration_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
