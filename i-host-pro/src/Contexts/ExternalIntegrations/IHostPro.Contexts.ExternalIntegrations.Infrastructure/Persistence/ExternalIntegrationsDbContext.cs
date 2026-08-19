using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

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
/// Deliberately does NOT call <c>ModelBuilder.MapWolverineEnvelopeStorage</c>
/// — External Integrations publishes no Integration Event this checkpoint
/// (CP2.1 mandate §8) — mirrors <c>CommunicationDbContext</c>'s own precedent
/// exactly.
/// </summary>
public sealed class ExternalIntegrationsDbContext : BaseDbContext
{
    public override string SchemaName => "external_integrations";

    public DbSet<WhatsAppIntegration> WhatsAppIntegrations => Set<WhatsAppIntegration>();
    public DbSet<WhatsAppTemplateMapping> WhatsAppTemplateMappings => Set<WhatsAppTemplateMapping>();

    public ExternalIntegrationsDbContext(DbContextOptions<ExternalIntegrationsDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExternalIntegrationsDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
