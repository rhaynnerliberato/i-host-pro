using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Property Management Bounded Context's own DbContext, owning the
/// `property_management` PostgreSQL schema ("Architecture Principles.md" §3,
/// §10; Checkpoint 0 plan, item 1). Inherits the mandatory tenant Global
/// Query Filter from <see cref="BaseDbContext"/> for every entity
/// implementing <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>;
/// Row-Level Security is applied at the database level as a second,
/// independent layer of defense (Checkpoint 1 plan, item 4).
///
/// <see cref="ModelBuilder.MapWolverineEnvelopeStorage(ModelBuilder, string?)"/>
/// (Checkpoint 6 homologação, third production defect fix) maps this
/// context's model to the pre-existing <c>property_management_messaging</c>
/// envelope tables — see <c>IdentityDbContext</c>'s own doc comment for the
/// full root-cause explanation (identical mechanism, mirrored here).
/// </summary>
public sealed class PropertyManagementDbContext : BaseDbContext
{
    public override string SchemaName => "property_management";

    public DbSet<Condominium> Condominiums => Set<Condominium>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<PropertyOwnerLink> PropertyOwnerLinks => Set<PropertyOwnerLink>();
    public DbSet<PropertyAuditEntry> PropertyAuditLog => Set<PropertyAuditEntry>();
    public DbSet<FrontDeskContact> FrontDeskContacts => Set<FrontDeskContact>();
    public DbSet<PropertyAccessConfiguration> PropertyAccessConfigurations => Set<PropertyAccessConfiguration>();

    public PropertyManagementDbContext(DbContextOptions<PropertyManagementDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every entity in this context is materialized through its domain
        // methods, never through public setters — mirrors IdentityDbContext.
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PropertyManagementDbContext).Assembly);

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "property_management_messaging", typeof(PropertyManagementDbContext))
        // schema literal exactly.
        modelBuilder.MapWolverineEnvelopeStorage("property_management_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
