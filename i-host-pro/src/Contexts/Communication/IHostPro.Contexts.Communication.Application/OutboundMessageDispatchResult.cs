namespace IHostPro.Contexts.Communication.Application;

/// <summary>The Connector's own technical outcome — never carries the destination/content back; the caller (the Application processor) already has them and decides the Message state transition.</summary>
public sealed record OutboundMessageDispatchResult(bool Success, string? FailureReason);
