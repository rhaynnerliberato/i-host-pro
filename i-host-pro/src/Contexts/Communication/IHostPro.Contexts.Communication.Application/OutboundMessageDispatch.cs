namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// The minimal payload a Connector needs — <see cref="Destination"/> carries
/// the guest's real phone number in-memory only, for the duration of this
/// one call; it is never logged and never returned to the caller inside
/// <see cref="OutboundMessageDispatchResult"/>.
/// </summary>
public sealed record OutboundMessageDispatch(string Destination, string Content, string IdempotencyKey);
