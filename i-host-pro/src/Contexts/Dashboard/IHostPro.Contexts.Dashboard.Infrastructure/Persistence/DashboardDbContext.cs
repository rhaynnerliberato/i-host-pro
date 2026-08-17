using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence;

/// <summary>
/// The Dashboard &amp; Reporting Bounded Context's own DbContext, owning the
/// <c>dashboard</c> PostgreSQL schema (Fase 7, Incremento 2 — Dashboard &amp;
/// Reporting Foundation, Checkpoint 1). Every entity here is a local,
/// tenant-aware read-model row mirroring another context's own Integration
/// Events — never a genuine owned aggregate (see
/// <c>Dashboard.Domain.AssemblyReference</c>'s own doc comment). Inherits
/// the mandatory tenant Global Query Filter from <see cref="BaseDbContext"/>
/// for every entity implementing <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>;
/// Row-Level Security is applied at the database level as a second,
/// independent layer of defense — mirrors <c>ReservationsDbContext</c>/
/// <c>HousekeepingDbContext</c> exactly.
///
/// <see cref="ModelBuilder.MapWolverineEnvelopeStorage(ModelBuilder, string?)"/>
/// maps this context's model to the <c>dashboard_messaging</c> envelope
/// tables — required for <c>IDbContextOutbox&lt;DashboardDbContext&gt;</c> to
/// resolve at all (Dashboard publishes no event of its own, but its
/// projection synchronizers still commit through the same outbox-transactional
/// SaveChanges pattern every other context uses).
/// </summary>
public sealed class DashboardDbContext : BaseDbContext
{
    public override string SchemaName => "dashboard";

    public DbSet<DashboardReservationProjectionEntry> ReservationProjection => Set<DashboardReservationProjectionEntry>();

    public DbSet<DashboardCleaningProjectionEntry> CleaningProjection => Set<DashboardCleaningProjectionEntry>();

    public DbSet<DashboardPropertyProjectionEntry> PropertyProjection => Set<DashboardPropertyProjectionEntry>();

    public DbSet<DashboardOccurrenceProjectionEntry> OccurrenceProjection => Set<DashboardOccurrenceProjectionEntry>();

    public DashboardDbContext(DbContextOptions<DashboardDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every entity in this context is materialized through its own
        // constructor/mutator methods, never through public setters —
        // mirrors ReservationsDbContext/HousekeepingDbContext.
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DashboardDbContext).Assembly);

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "dashboard_messaging", typeof(DashboardDbContext)) schema literal
        // exactly.
        modelBuilder.MapWolverineEnvelopeStorage("dashboard_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
