namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// One page of a mailbox delta synchronization. Exactly one of
/// <see cref="NextLink"/>/<see cref="DeltaLink"/> is set: <see cref="NextLink"/>
/// means more pages remain, <see cref="DeltaLink"/> means this was the final
/// page and the synchronization window is complete. Both are opaque
/// provider-issued values — never parsed, rewritten, or reconstructed
/// (Fase 9 review §7).
/// </summary>
public sealed record AirbnbEmailDeltaPage(
    IReadOnlyList<AirbnbEmailMessageSummary> Messages,
    string? NextLink,
    string? DeltaLink);
