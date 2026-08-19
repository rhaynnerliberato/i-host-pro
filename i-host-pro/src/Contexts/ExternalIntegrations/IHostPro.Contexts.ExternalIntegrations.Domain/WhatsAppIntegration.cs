using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// A tenant's WhatsApp provider configuration (Fase 9, Checkpoint 2.1 —
/// External Integrations + Credential/Configuration Foundation). One
/// integration per tenant in the MVP (CP2.0 audit, Decisão E, approved by
/// the CP2.1 mandate §15) — never multi-number/multi-WABA/multi-provider in
/// this checkpoint; the schema allows future evolution without a framework
/// built ahead of need.
///
/// Holds only non-secret identifiers (<see cref="WabaId"/>,
/// <see cref="PhoneNumberId"/>) and opaque secret REFERENCES — never a
/// secret value itself (CP2.1 mandate §13/§14: Access Token/App Secret/
/// Verify Token are classified SECRET and are never persisted here; the
/// real value is resolved at runtime, outside this aggregate, by
/// <c>IWhatsAppCredentialProvider</c> from the reference).
///
/// <see cref="IsEnabled"/> is set once at <see cref="Create"/> to
/// <c>false</c> and never changes for the entire lifetime of this
/// checkpoint — deliberately no <c>Enable()</c>/<c>Disable()</c> operation
/// is exposed here (CP2.1 mandate §18): enabling must eventually respect
/// provider credentials being valid, a Production secret provider existing,
/// and a resolved consent decision, none of which exist yet. Exposing an
/// early bypass would be worse than not having the capability at all.
/// </summary>
public sealed class WhatsAppIntegration : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string? WabaId { get; private set; }
    public string? PhoneNumberId { get; private set; }
    public bool IsEnabled { get; private set; }
    public string? AccessTokenSecretReference { get; private set; }
    public string? AppSecretSecretReference { get; private set; }
    public string? VerifyTokenSecretReference { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private WhatsAppIntegration()
    {
        // EF Core materialization.
    }

    private WhatsAppIntegration(Guid id, Guid tenantId, DateTimeOffset createdAtUtc) : base(id)
    {
        TenantId = tenantId;
        IsEnabled = false;
        CreatedAtUtc = createdAtUtc;
    }

    public static WhatsAppIntegration Create(Guid id, Guid tenantId, DateTimeOffset createdAtUtc) =>
        new(id, tenantId, createdAtUtc);

    /// <summary>
    /// Updates the non-secret identifiers and secret references. Never
    /// touches <see cref="IsEnabled"/> — no path in this checkpoint changes
    /// enablement (CP2.1 mandate §18).
    /// </summary>
    public void UpdateConfiguration(
        string? wabaId,
        string? phoneNumberId,
        string? accessTokenSecretReference,
        string? appSecretSecretReference,
        string? verifyTokenSecretReference,
        DateTimeOffset updatedAtUtc)
    {
        WabaId = wabaId;
        PhoneNumberId = phoneNumberId;
        AccessTokenSecretReference = accessTokenSecretReference;
        AppSecretSecretReference = appSecretSecretReference;
        VerifyTokenSecretReference = verifyTokenSecretReference;
        UpdatedAtUtc = updatedAtUtc;
    }
}
