namespace IHostPro.Contexts.Communication.Domain;

/// <summary>
/// The provider-reported status <see cref="Message.ApplyProviderStatus"/>
/// accepts (Fase 9, Checkpoint 2.3.3, ADR-022 item 14). Deliberately a
/// Communication-local type, never a reference to
/// <c>ExternalIntegrations.Contracts.WhatsAppMessageProviderStatus</c> —
/// Domain never references another Bounded Context's surface, even its
/// public Contracts (that reference belongs exclusively to the
/// Application-layer Wolverine consumer that maps one to the other). Small,
/// deliberate duplication (mandate §34) — not a shared BuildingBlock.
/// </summary>
public enum WhatsAppProviderStatus
{
    Sent,
    Delivered,
    Read,
    Failed,
}
