using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Payments.Infrastructure.Persistence;

/// <summary>
/// The Payments Bounded Context's own DbContext, owning the
/// <c>payments</c> PostgreSQL schema (Fase 10, Checkpoint 5 — PIX/Payment
/// Deterministic Foundation). Inherits the mandatory tenant Global Query
/// Filter from <see cref="BaseDbContext"/>; Row-Level Security is applied at
/// the database level as a second, independent layer of defense — mirrors
/// <c>GuestOperationsDbContext</c> exactly.
/// </summary>
public sealed class PaymentsDbContext : BaseDbContext
{
    public override string SchemaName => "payments";

    public DbSet<PixCharge> PixCharges => Set<PixCharge>();

    public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every entity in this context is materialized through its domain
        // methods, never through public setters — mirrors every other
        // context's own DbContext.
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "payments_messaging", typeof(PaymentsDbContext)) schema literal exactly.
        modelBuilder.MapWolverineEnvelopeStorage("payments_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
