using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Domain;

/// <summary>
/// The operational contact ("Portaria") notified for a Condominium's
/// guest-arrival/authorization events (Fase 10, Checkpoint 4 — Portaria
/// Notification Foundation). At most one row exists per
/// <c>(TenantId, CondominiumId)</c> — enforced by a database unique
/// constraint, never a Domain-level check — updated in place, never
/// soft-deleted/re-created: <see cref="IsActive"/> is the sole on/off
/// toggle, so disabling a contact never loses its configured
/// <see cref="DisplayName"/>/<see cref="PhoneNumber"/>.
///
/// Deliberately carries only operational contact fields — no guest data, no
/// access credential, no provider-specific identifier (e.g. a WhatsApp
/// phone-number-id) — <see cref="PhoneNumber"/> is a plain operational
/// contact number, resolved by Communication through the new synchronous
/// exception #9 (<c>IFrontDeskContactReader</c>, ADR-026), never a
/// provider-neutral/provider-specific distinction this entity needs to
/// know about.
/// </summary>
public sealed class FrontDeskContact : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid CondominiumId { get; private set; }
    public string DisplayName { get; private set; } = null!;
    public string PhoneNumber { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private FrontDeskContact()
    {
        // EF Core materialization.
    }

    private FrontDeskContact(
        Guid id, Guid tenantId, Guid condominiumId, string displayName, string phoneNumber, bool isActive, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        CondominiumId = condominiumId;
        DisplayName = displayName;
        PhoneNumber = phoneNumber;
        IsActive = isActive;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static FrontDeskContact Create(
        Guid id, Guid tenantId, Guid condominiumId, string displayName, string phoneNumber, bool isActive, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name cannot be empty.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(phoneNumber))
            throw new ArgumentException("Phone number cannot be empty.", nameof(phoneNumber));

        return new FrontDeskContact(id, tenantId, condominiumId, displayName.Trim(), phoneNumber.Trim(), isActive, now);
    }

    /// <summary>
    /// Replaces this contact's configured fields wholesale — the caller
    /// (<c>SetFrontDeskContactCommandHandler</c>) is responsible for the
    /// no-op comparison, mirroring <c>Condominium.Rename</c>/<c>ChangeAddress</c>.
    /// </summary>
    public void UpdateContact(string displayName, string phoneNumber, bool isActive, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name cannot be empty.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(phoneNumber))
            throw new ArgumentException("Phone number cannot be empty.", nameof(phoneNumber));

        DisplayName = displayName.Trim();
        PhoneNumber = phoneNumber.Trim();
        IsActive = isActive;
        UpdatedAtUtc = now;
    }
}
