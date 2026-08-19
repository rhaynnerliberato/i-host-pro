namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// The Connector's own technical outcome — never carries the destination/
/// content back; the caller (the Application processor) already has them and
/// decides the Message state transition. <see cref="ProviderMessageId"/>
/// (Fase 9, Checkpoint 2.2) is the provider's own opaque message identifier
/// (e.g. a WhatsApp <c>wamid</c>) — always <see langword="null"/> when
/// <see cref="Success"/> is <see langword="false"/>, and always
/// <see langword="null"/> for the fake connector (which reports no real
/// provider id).
/// </summary>
public sealed record OutboundMessageDispatchResult(bool Success, string? ProviderMessageId, string? FailureReason);
