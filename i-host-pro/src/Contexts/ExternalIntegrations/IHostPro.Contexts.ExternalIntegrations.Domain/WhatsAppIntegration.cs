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
/// <c>false</c> and stays <c>false</c> until an explicit, separately
/// authorized <see cref="Enable"/> call (Real Tenant WhatsApp Activation
/// Readiness gate — SMALL_IMPLEMENTATION_GAP plan approved after the CP2.1
/// mandate §18 freeze). <see cref="UpdateConfiguration"/> never enables or
/// disables this aggregate implicitly — Configure and Enable/Disable are
/// always distinct actions, never one hidden inside the other.
/// <see cref="Enable"/> asserts only the LOCAL invariants this aggregate can
/// see for itself (the identifiers/references below are present); whether
/// the referenced secret VALUES actually resolve is an external/
/// Infrastructure concern checked separately by the Application layer —
/// this aggregate must never depend on AWS/Secrets Manager.
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

    /// <summary>
    /// <see langword="false"/> → <see langword="true"/>. Requires
    /// <see cref="WabaId"/>, <see cref="PhoneNumberId"/> and all three
    /// secret references to already be present (via
    /// <see cref="UpdateConfiguration"/>) — an integration can never be
    /// enabled with an incomplete configuration. Deliberately never
    /// validates the referenced secret VALUES themselves (see this class's
    /// own remarks) — that preflight belongs to the Application layer.
    /// </summary>
    public void Enable(DateTimeOffset updatedAtUtc)
    {
        if (IsEnabled)
            throw new InvalidOperationException("WhatsApp integration is already enabled.");

        if (string.IsNullOrWhiteSpace(WabaId) ||
            string.IsNullOrWhiteSpace(PhoneNumberId) ||
            string.IsNullOrWhiteSpace(AccessTokenSecretReference) ||
            string.IsNullOrWhiteSpace(AppSecretSecretReference) ||
            string.IsNullOrWhiteSpace(VerifyTokenSecretReference))
        {
            throw new InvalidOperationException("Cannot enable a WhatsApp integration with incomplete configuration.");
        }

        IsEnabled = true;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>
    /// <see langword="true"/> → <see langword="false"/>. Always allowed
    /// regardless of the current configuration's completeness — disabling
    /// must never be blocked by the same precondition that guards
    /// <see cref="Enable"/>.
    /// </summary>
    public void Disable(DateTimeOffset updatedAtUtc)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("WhatsApp integration is already disabled.");

        IsEnabled = false;
        UpdatedAtUtc = updatedAtUtc;
    }
}
