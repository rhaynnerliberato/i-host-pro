using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <summary>
/// The Housekeeping Bounded Context's own DbContext, owning the
/// <c>housekeeping</c> PostgreSQL schema (Fase 6, Incremento 1). Inherits
/// the mandatory tenant Global Query Filter from <see cref="BaseDbContext"/>
/// for every entity implementing <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>;
/// Row-Level Security is applied at the database level as a second,
/// independent layer of defense — mirrors <c>ReservationsDbContext</c>
/// exactly.
///
/// <see cref="ModelBuilder.MapWolverineEnvelopeStorage(ModelBuilder, string?)"/>
/// maps this context's model to the <c>housekeeping_messaging</c> envelope
/// tables — applying the Checkpoint 6 (Fase 2) Wolverine composition fix
/// from day one.
/// </summary>
public sealed class HousekeepingDbContext : BaseDbContext
{
    public override string SchemaName => "housekeeping";

    public DbSet<Cleaning> Cleanings => Set<Cleaning>();
    public DbSet<CleaningAuditEntry> CleaningAuditLog => Set<CleaningAuditEntry>();
    public DbSet<PropertyProjectionEntry> PropertyProjection => Set<PropertyProjectionEntry>();
    public DbSet<ReservationProjectionEntry> ReservationProjection => Set<ReservationProjectionEntry>();

    public HousekeepingDbContext(DbContextOptions<HousekeepingDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every entity in this context is materialized through its domain
        // methods, never through public setters — mirrors
        // ReservationsDbContext/PropertyManagementDbContext.
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HousekeepingDbContext).Assembly);

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "housekeeping_messaging", typeof(HousekeepingDbContext)) schema
        // literal exactly.
        modelBuilder.MapWolverineEnvelopeStorage("housekeeping_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
