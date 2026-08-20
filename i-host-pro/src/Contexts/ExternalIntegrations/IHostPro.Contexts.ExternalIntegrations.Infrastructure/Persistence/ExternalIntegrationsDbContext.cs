using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

/// <summary>
/// The External Integrations Bounded Context's own DbContext, owning the
/// <c>external_integrations</c> PostgreSQL schema (Fase 9, Checkpoint 2.1).
/// Inherits the mandatory tenant Global Query Filter from
/// <see cref="BaseDbContext"/> for <see cref="WhatsAppIntegration"/>
/// (implements <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>);
/// Row-Level Security is applied at the database level as a second,
/// independent layer of defense — mirrors every other Bounded Context's own
/// DbContext exactly.
///
/// Fase 9, Checkpoint 2.3.3 (ADR-022 item 13): now calls
/// <see cref="ModelBuilder.MapWolverineEnvelopeStorage(ModelBuilder, string?)"/>
/// — External Integrations publishes its first Integration Event
/// (<c>WhatsAppMessageStatusChanged</c>) this checkpoint, so
/// <c>IDbContextOutbox&lt;ExternalIntegrationsDbContext&gt;</c> is now
/// needed. The schema literal below must match
/// <c>EnrollAncillaryPostgresqlOutbox(..., "external_integrations_messaging", ...)</c>
/// in <c>IHostPro.Api</c>'s/<c>IHostPro.MigrationRunner</c>'s own
/// <c>Program.cs</c> exactly — mirrors <c>ReservationsDbContext</c>'s own
/// precedent and its own doc comment's warning.
/// </summary>
public sealed class ExternalIntegrationsDbContext : BaseDbContext
{
    public override string SchemaName => "external_integrations";

    public DbSet<WhatsAppIntegration> WhatsAppIntegrations => Set<WhatsAppIntegration>();
    public DbSet<WhatsAppTemplateMapping> WhatsAppTemplateMappings => Set<WhatsAppTemplateMapping>();

    /// <summary>Global, non-tenant-owned — see <see cref="Mappings.WhatsAppTenantRouteConfiguration"/>'s remarks.</summary>
    public DbSet<WhatsAppTenantRoute> WhatsAppTenantRoutes => Set<WhatsAppTenantRoute>();

    public ExternalIntegrationsDbContext(DbContextOptions<ExternalIntegrationsDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExternalIntegrationsDbContext).Assembly);

        modelBuilder.MapWolverineEnvelopeStorage("external_integrations_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
