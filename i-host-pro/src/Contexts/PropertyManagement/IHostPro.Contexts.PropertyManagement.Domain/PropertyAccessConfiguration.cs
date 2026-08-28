using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Domain;

/// <summary>
/// A Property's guest-access configuration (Fase 10, Checkpoint 6.2 — Guest
/// Access Secure Delivery). At most one row exists per
/// <c>(TenantId, PropertyId)</c> — enforced by a database unique constraint,
/// never a Domain-level check — updated in place, never soft-deleted/
/// re-created: <see cref="IsActive"/> is the sole on/off toggle, mirrors
/// <see cref="FrontDeskContact"/>'s own convention exactly.
///
/// MVP decision (CP6.1 Decision Gate): a single FIXED credential per
/// Property, configured manually by an administrator — no per-Reservation/
/// per-stay generation, no Smart Lock integration, no provider. Only
/// <see cref="AccessCredentialSecretReference"/> is persisted here — never
/// the raw credential value (resolved at delivery time, in memory only, via
/// <c>IPropertyAccessCredentialProvider</c>). <see cref="AccessInstructions"/>
/// is deliberately NOT a secret — plain operational content (Documento 12
/// §5: "Imóvel" possui "fechadura" and "instruções" as two distinct
/// attributes) — persisted and handled like any other Communication message
/// content.
/// </summary>
public sealed class PropertyAccessConfiguration : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string? AccessCredentialSecretReference { get; private set; }
    public string? AccessInstructions { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private PropertyAccessConfiguration()
    {
        // EF Core materialization.
    }

    private PropertyAccessConfiguration(
        Guid id, Guid tenantId, Guid propertyId, string? accessCredentialSecretReference,
        string? accessInstructions, bool isActive, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        PropertyId = propertyId;
        AccessCredentialSecretReference = accessCredentialSecretReference;
        AccessInstructions = accessInstructions;
        IsActive = isActive;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static PropertyAccessConfiguration Create(
        Guid id, Guid tenantId, Guid propertyId, string? accessCredentialSecretReference,
        string? accessInstructions, bool isActive, DateTimeOffset now) =>
        new(id, tenantId, propertyId, Normalize(accessCredentialSecretReference), Normalize(accessInstructions), isActive, now);

    /// <summary>
    /// Replaces this configuration's fields wholesale — the caller
    /// (<c>SetPropertyAccessConfigurationCommandHandler</c>) is responsible
    /// for the no-op comparison, mirrors <see cref="FrontDeskContact.UpdateContact"/>.
    /// </summary>
    public void UpdateConfiguration(
        string? accessCredentialSecretReference, string? accessInstructions, bool isActive, DateTimeOffset now)
    {
        AccessCredentialSecretReference = Normalize(accessCredentialSecretReference);
        AccessInstructions = Normalize(accessInstructions);
        IsActive = isActive;
        UpdatedAtUtc = now;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
