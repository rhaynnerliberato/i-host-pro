namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Minimal, provider-neutral metadata for one mailbox message observed during
/// delta polling — deliberately excludes the message body (Fase 9 review
/// §15-16: routine polling must not overfetch; body is only read on-demand,
/// separately, for the bounded discovery sample).
/// </summary>
public sealed record AirbnbEmailMessageSummary(
    string MessageId,
    string? InternetMessageId,
    DateTimeOffset ReceivedAtUtc,
    string? Subject,
    string? FromAddress,
    string? SenderAddress,
    string? BodyPreview);
