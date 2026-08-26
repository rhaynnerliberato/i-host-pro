using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// A tenant's Airbnb provider configuration (Fase 9, Checkpoint 3.2 —
/// "Airbnb Deterministic Foundation"). Mirrors <see cref="WhatsAppIntegration"/>
/// exactly: one integration per tenant (CP3.1 Decision Gate item B), holds
/// only a non-secret identifier plus an opaque secret REFERENCE — never a
/// secret value itself; none exists yet, since no Airbnb partner contract is
/// available (<c>AirbnbPartnerAccessAvailable=false</c>).
///
/// <see cref="IsEnabled"/> is set once at <see cref="Create"/> to
/// <c>false</c> and never changes this checkpoint — deliberately no
/// <c>Enable()</c>/<c>Disable()</c> operation is exposed (mirrors
/// <see cref="WhatsAppIntegration"/>'s own CP2.1 rationale): enabling would
/// require a real Airbnb connector and partner credentials, neither of which
/// exist.
/// </summary>
public sealed class AirbnbIntegration : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string? ExternalAccountId { get; private set; }
    public bool IsEnabled { get; private set; }
    public string? CredentialSecretReference { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private AirbnbIntegration()
    {
        // EF Core materialization.
    }

    private AirbnbIntegration(Guid id, Guid tenantId, DateTimeOffset createdAtUtc) : base(id)
    {
        TenantId = tenantId;
        IsEnabled = false;
        CreatedAtUtc = createdAtUtc;
    }

    public static AirbnbIntegration Create(Guid id, Guid tenantId, DateTimeOffset createdAtUtc) =>
        new(id, tenantId, createdAtUtc);

    /// <summary>Never touches <see cref="IsEnabled"/> — no path this checkpoint changes enablement.</summary>
    public void UpdateConfiguration(
        string? externalAccountId, string? credentialSecretReference, DateTimeOffset updatedAtUtc)
    {
        ExternalAccountId = externalAccountId;
        CredentialSecretReference = credentialSecretReference;
        UpdatedAtUtc = updatedAtUtc;
    }
}
