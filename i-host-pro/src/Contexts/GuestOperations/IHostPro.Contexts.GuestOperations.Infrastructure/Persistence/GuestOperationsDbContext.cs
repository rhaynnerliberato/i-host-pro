using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.GuestOperations.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;

/// <summary>
/// The Guest Operations Bounded Context's own DbContext, owning the
/// <c>guest_operations</c> PostgreSQL schema (Fase 10, Checkpoint 1 — Guest
/// Operations Foundation). Inherits the mandatory tenant Global Query Filter
/// from <see cref="BaseDbContext"/>; Row-Level Security is applied at the
/// database level as a second, independent layer of defense — mirrors
/// <c>ReservationsDbContext</c> exactly.
/// </summary>
public sealed class GuestOperationsDbContext : BaseDbContext
{
    public override string SchemaName => "guest_operations";

    public DbSet<GuestStayOperation> GuestStayOperations => Set<GuestStayOperation>();

    public DbSet<EarlyCheckInRequest> EarlyCheckInRequests => Set<EarlyCheckInRequest>();

    public DbSet<LateCheckoutRequest> LateCheckoutRequests => Set<LateCheckoutRequest>();

    public GuestOperationsDbContext(DbContextOptions<GuestOperationsDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every entity in this context is materialized through its domain
        // methods, never through public setters — mirrors
        // ReservationsDbContext/PropertyManagementDbContext.
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GuestOperationsDbContext).Assembly);

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "guest_operations_messaging", typeof(GuestOperationsDbContext))
        // schema literal exactly.
        modelBuilder.MapWolverineEnvelopeStorage("guest_operations_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
