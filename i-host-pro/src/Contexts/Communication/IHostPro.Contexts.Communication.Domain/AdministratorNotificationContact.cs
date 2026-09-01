using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Communication.Domain;

/// <summary>
/// The tenant-scoped recipient for real human-handoff operational
/// notifications (Fase 11, Checkpoint 6, mandate item 18-20) — deliberately
/// distinct from <c>PropertyManagement.FrontDeskContact</c> (Portaria, scoped
/// per-Property, resolved by Communication for guest-facing operational
/// events) and from <c>Identity.User</c> (has no phone field at all, and
/// authentication identity is a different concern from a messaging
/// destination). Communication owns this entity end-to-end — recipient,
/// channel, and destination never leave this Bounded Context (CP6 mandate
/// item 19/21): the AI Agent never resolves, stores, or receives this
/// contact's own phone number.
///
/// MVP cardinality (mandate item 20): at most one ACTIVE contact per Tenant
/// — enforced by a partial unique index (see the Infrastructure mapping), no
/// list, no round-robin, no assignment. <see cref="DestinationPhone"/> is
/// WhatsApp-only this checkpoint (Communication's only real outbound
/// channel), so no separate <c>Channel</c> property exists yet — adding one
/// is a future checkpoint's decision, not invented here.
/// </summary>
public sealed class AdministratorNotificationContact : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string DestinationPhone { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private AdministratorNotificationContact()
    {
        // EF Core materialization.
    }

    private AdministratorNotificationContact(Guid id, Guid tenantId, string destinationPhone, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        DestinationPhone = destinationPhone;
        IsActive = true;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static AdministratorNotificationContact Create(Guid id, Guid tenantId, string destinationPhone, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(destinationPhone))
            throw new ArgumentException("Destination phone cannot be empty.", nameof(destinationPhone));

        return new AdministratorNotificationContact(id, tenantId, destinationPhone, now);
    }

    /// <summary>Replaces the destination phone in place — the Application layer never creates a second row for the same Tenant while one is active (the partial unique index backstops this).</summary>
    public void ChangeDestinationPhone(string destinationPhone, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(destinationPhone))
            throw new ArgumentException("Destination phone cannot be empty.", nameof(destinationPhone));

        DestinationPhone = destinationPhone;
        UpdatedAtUtc = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAtUtc = now;
    }

    public void Reactivate(DateTimeOffset now)
    {
        IsActive = true;
        UpdatedAtUtc = now;
    }
}
