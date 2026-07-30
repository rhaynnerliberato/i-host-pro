using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when a user's password is changed — either by the user
/// themselves (requires their current password) or reset by an Administrator
/// on their behalf (requires no current password) — never for any other
/// authentication-adjacent action (Incremento 3 plan, Checkpoint 1; Documento
/// 07 §13.3). Always published alongside one <see cref="SessionRevoked"/>
/// (reason code <c>password_changed</c>) per session that was active at the
/// time, including the session that originated a self-service change — the
/// user must authenticate again afterward either way; the response to that
/// request never returns new tokens automatically (Incremento 3 planning,
/// approved decision).
///
/// <see cref="ChangeType"/> is one of the stable ASCII codes in
/// <c>PasswordChangeTypeCodes</c> (<c>self</c>, <c>admin_reset</c>). Never
/// carries the password itself, its hash, or any derivative.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the affected user's id/<c>"User"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is that same user for a self-service change, or the resetting
/// Administrator for an admin reset.
/// </summary>
public sealed record PasswordChanged : IntegrationEvent
{
    public required string ChangeType { get; init; }
}
