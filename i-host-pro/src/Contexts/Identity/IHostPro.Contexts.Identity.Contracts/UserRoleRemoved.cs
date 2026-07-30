using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when a role is removed from a user (Incremento 3 plan,
/// Checkpoint 1; Documento 07 §13.3). Rejected outright — no event published,
/// no state changed — when the removal would leave the tenant without any
/// active Administrator (last-Administrator guard, Incremento 3 planning).
/// Always published alongside one <see cref="SessionRevoked"/> (reason code
/// <c>roles_changed</c>) per session that was active at the time, forcing
/// re-authentication so the JWT <c>role</c> claim reflects the change
/// immediately.
///
/// <see cref="RoleCode"/> is the platform's stable role code (e.g.
/// <c>"ADMIN"</c>, Documento 09 §4) — never a display name.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the affected user's id/<c>"User"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator who removed the role.
/// </summary>
public sealed record UserRoleRemoved : IntegrationEvent
{
    public required string RoleCode { get; init; }
}
