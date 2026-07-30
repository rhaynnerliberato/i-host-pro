using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published whenever a session is revoked. <see cref="ReasonCode"/> is one
/// of the stable ASCII codes in <c>SessionRevokedReasonCodes</c>. Three
/// triggers actually publish this event as of Incremento 3, Checkpoint 4
/// (Documento 07 §13.1) — <c>logout_requested</c> (alongside
/// <see cref="UserLoggedOut"/>, Incremento 2), <c>refresh_token_reuse_detected</c>
/// (alongside <see cref="RefreshTokenReuseDetected"/>, Incremento 2) and
/// <c>user_requested_revocation</c> (self-service revocation of a specific
/// session, no other event in the same transaction — Incremento 3, Checkpoint
/// 4). <c>user_blocked</c>/<c>roles_changed</c> are published since Checkpoints
/// 6/7, and <c>password_changed</c> since Checkpoint 9 — by
/// <c>ChangeOwnPasswordCommand</c>/<c>AdminResetPasswordCommand</c> (Documento
/// 07 §13.3).
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the user's id/<c>"User"</c>.
/// </summary>
public sealed record SessionRevoked : IntegrationEvent
{
    public required Guid SessionId { get; init; }

    public required string ReasonCode { get; init; }
}
