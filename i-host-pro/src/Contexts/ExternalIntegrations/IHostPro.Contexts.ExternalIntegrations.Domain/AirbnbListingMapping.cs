using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Maps one Airbnb listing to one internal Property, for one tenant (Fase 9,
/// Checkpoint 3.2 — "Airbnb Deterministic Foundation"; CP3.1 Decision Gate
/// item D). Tenant-owned, RLS-protected — deliberately NOT a global routing
/// directory like <c>WhatsAppTenantRoute</c>: no known Airbnb webhook
/// contract requires resolving a tenant before it is known (CP3.1 Decision
/// Gate item 5's own conditional-reevaluation note). <see cref="PropertyId"/>
/// carries no physical foreign key to <c>property_management.properties</c>
/// — same opaque-Guid convention <c>Reservation.PropertyId</c> already uses
/// across this exact boundary.
/// </summary>
public sealed class AirbnbListingMapping : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid AirbnbIntegrationId { get; private set; }
    public string ExternalListingId { get; private set; } = null!;
    public Guid PropertyId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private AirbnbListingMapping()
    {
        // EF Core materialization.
    }

    private AirbnbListingMapping(
        Guid id, Guid tenantId, Guid airbnbIntegrationId, string externalListingId, Guid propertyId,
        DateTimeOffset createdAtUtc) : base(id)
    {
        TenantId = tenantId;
        AirbnbIntegrationId = airbnbIntegrationId;
        ExternalListingId = externalListingId;
        PropertyId = propertyId;
        CreatedAtUtc = createdAtUtc;
    }

    public static AirbnbListingMapping Create(
        Guid id, Guid tenantId, Guid airbnbIntegrationId, string externalListingId, Guid propertyId,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(externalListingId))
            throw new ArgumentException("External listing id cannot be empty.", nameof(externalListingId));

        return new AirbnbListingMapping(id, tenantId, airbnbIntegrationId, externalListingId.Trim(), propertyId, createdAtUtc);
    }

    public void ChangePropertyId(Guid newPropertyId, DateTimeOffset updatedAtUtc)
    {
        PropertyId = newPropertyId;
        UpdatedAtUtc = updatedAtUtc;
    }
}
