using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when an already-rotated refresh token is presented again
/// outside <c>IRefreshTokenRotationPolicy.ConcurrentRotationGraceWindow</c> —
/// never for a presentation within the grace window, which is treated as a
/// benign concurrent-retry race and publishes nothing (Incremento 2 plan,
/// Etapa 15; Documento 07 §13.1). Always published alongside a
/// <see cref="SessionRevoked"/> (reason code <c>refresh_token_reuse_detected</c>)
/// for the same session.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the user's id/<c>"User"</c>. <see cref="TokenId"/> is the refresh
/// token's public identifier only — never its hash and never the full
/// presented token string.
/// </summary>
public sealed record RefreshTokenReuseDetected : IntegrationEvent
{
    public required Guid SessionId { get; init; }

    public required Guid TokenId { get; init; }
}
