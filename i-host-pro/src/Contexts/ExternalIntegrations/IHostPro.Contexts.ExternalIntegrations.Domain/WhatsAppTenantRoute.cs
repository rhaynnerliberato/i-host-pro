using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Maps a Meta-issued <see cref="PhoneNumberId"/> to the <see cref="TenantId"/>
/// that owns it (Fase 9, Checkpoint 2.3.2 — ADR-022, items 10/11). Deliberately
/// NOT <see cref="ITenantOwned"/> and carries no RLS policy — it exists
/// specifically to solve the tenant-bootstrap problem the webhook has:
/// resolving "which tenant" from a caller-supplied identifier BEFORE any
/// <c>TenantId</c> is known, which a tenant-owned/RLS-protected table cannot
/// do by definition. Holds only identifiers — never a secret, never a raw
/// phone number, never webhook payload data (ADR-022 item 8).
///
/// One active route per tenant (mirrors <see cref="WhatsAppIntegration"/>'s
/// own one-integration-per-tenant invariant) and globally unique
/// <see cref="PhoneNumberId"/> — enforced by unique indexes, never just an
/// application-level check, matching every other uniqueness rule already
/// established in this Bounded Context.
/// </summary>
public sealed class WhatsAppTenantRoute : AggregateRoot<Guid>
{
    public string PhoneNumberId { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private WhatsAppTenantRoute()
    {
        // EF Core materialization.
    }

    private WhatsAppTenantRoute(Guid id, string phoneNumberId, Guid tenantId, DateTimeOffset createdAtUtc) : base(id)
    {
        PhoneNumberId = phoneNumberId;
        TenantId = tenantId;
        CreatedAtUtc = createdAtUtc;
    }

    public static WhatsAppTenantRoute Create(Guid id, string phoneNumberId, Guid tenantId, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
            throw new ArgumentException("Phone number id cannot be empty.", nameof(phoneNumberId));

        return new WhatsAppTenantRoute(id, phoneNumberId, tenantId, createdAtUtc);
    }

    public void UpdatePhoneNumberId(string phoneNumberId, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
            throw new ArgumentException("Phone number id cannot be empty.", nameof(phoneNumberId));

        PhoneNumberId = phoneNumberId;
        UpdatedAtUtc = updatedAtUtc;
    }
}
