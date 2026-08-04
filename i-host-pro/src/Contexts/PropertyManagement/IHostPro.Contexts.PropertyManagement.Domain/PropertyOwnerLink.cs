using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Domain;

/// <summary>
/// A many-to-many association between a Property and an owner (Checkpoint 0
/// plan, item 8). <see cref="OwnerUserId"/> is an opaque reference to an
/// Identity &amp; Access <c>User</c> — never a physical foreign key across
/// Bounded Contexts, per "Architecture Principles.md" §7/§14. Created and
/// removed exclusively by <c>PROPERTIES:MANAGE</c>-holding actors, after the
/// synchronous eligibility check against Identity's public contract
/// (Checkpoint 5 — not implemented yet; this increment only carries the
/// physical shape of the link).
/// </summary>
public sealed class PropertyOwnerLink : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private PropertyOwnerLink()
    {
        // EF Core materialization.
    }

    private PropertyOwnerLink(
        Guid id, Guid tenantId, Guid propertyId, Guid ownerUserId, Guid createdByUserId, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        PropertyId = propertyId;
        OwnerUserId = ownerUserId;
        CreatedByUserId = createdByUserId;
        CreatedAt = now;
    }

    public static PropertyOwnerLink Create(
        Guid id, Guid tenantId, Guid propertyId, Guid ownerUserId, Guid createdByUserId, DateTimeOffset now) =>
        new(id, tenantId, propertyId, ownerUserId, createdByUserId, now);
}
