using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <summary>
/// The Reservations Bounded Context's own DbContext, owning the
/// <c>reservations</c> PostgreSQL schema (Fase 3, Incremento 1 plan).
/// Inherits the mandatory tenant Global Query Filter from
/// <see cref="BaseDbContext"/> for every entity implementing
/// <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>; Row-Level
/// Security is applied at the database level as a second, independent layer
/// of defense — mirrors <c>PropertyManagementDbContext</c> exactly.
///
/// <see cref="ModelBuilder.MapWolverineEnvelopeStorage(ModelBuilder, string?)"/>
/// maps this context's model to the <c>reservations_messaging</c> envelope
/// tables — applying the Checkpoint 6 (Fase 2) Wolverine composition fix
/// from day one, never reproducing the "envelopes silently persisted to the
/// Main Store" defect it corrected.
/// </summary>
public sealed class ReservationsDbContext : BaseDbContext
{
    public override string SchemaName => "reservations";

    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationAuditEntry> ReservationAuditLog => Set<ReservationAuditEntry>();

    public ReservationsDbContext(DbContextOptions<ReservationsDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every entity in this context is materialized through its domain
        // methods, never through public setters — mirrors
        // PropertyManagementDbContext/IdentityDbContext.
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReservationsDbContext).Assembly);

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "reservations_messaging", typeof(ReservationsDbContext)) schema
        // literal exactly.
        modelBuilder.MapWolverineEnvelopeStorage("reservations_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
