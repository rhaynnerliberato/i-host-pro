using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Projections;

/// <summary>
/// This context's own local, tenant-aware read-model row for a single
/// Property Management property — built exclusively from consuming
/// <c>PropertyCreated</c>/<c>PropertyActivated</c>/<c>PropertyDeactivated</c>/
/// <c>PropertyArchived</c> (Checkpoint 0/3 gate). Deliberately carries only
/// <see cref="IsActive"/> — no name/code/address/capacity — nothing this
/// increment needs it; a physically separate table from
/// <c>property_management.properties</c>, never a foreign key across
/// contexts. Infrastructure-only persistence model (never referenced from
/// Application/Domain) — <see cref="IPropertyReferenceProjection"/> in
/// <c>Housekeeping.Application</c> is the port this entity backs.
/// </summary>
public sealed class PropertyProjectionEntry : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid PropertyId { get; private set; }
    public bool IsActive { get; private set; }

    private PropertyProjectionEntry()
    {
        // EF Core materialization.
    }

    public PropertyProjectionEntry(Guid tenantId, Guid propertyId, bool isActive)
    {
        TenantId = tenantId;
        PropertyId = propertyId;
        IsActive = isActive;
    }

    public void SetActive(bool isActive) => IsActive = isActive;
}
