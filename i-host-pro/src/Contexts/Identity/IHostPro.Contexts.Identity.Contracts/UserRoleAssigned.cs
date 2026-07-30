using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when a role is assigned to a user — including the mandatory
/// initial role at creation time (alongside <see cref="UserCreated"/>) and
/// every subsequent assignment by an Administrator (Incremento 3 plan,
/// Checkpoint 1; Documento 07 §13.3). Deliberately distinct from
/// <see cref="UserUpdated"/> — an authorization-relevant change is never
/// folded into a generic "profile updated" fact, and deliberately not named
/// <c>PermissionChanged</c> — the fixed permission catalog itself is
/// unaffected by a role assignment (Incremento 3 planning, approved
/// decision). Always published alongside one <see cref="SessionRevoked"/>
/// (reason code <c>roles_changed</c>) per session that was active at the
/// time, forcing re-authentication so the JWT <c>role</c> claim reflects the
/// change immediately.
///
/// <see cref="RoleCode"/> is the platform's stable role code (e.g.
/// <c>"ADMIN"</c>, Documento 09 §4) — never a display name.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the affected user's id/<c>"User"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator who performed the assignment, including the creating
/// Administrator for the initial role.
/// </summary>
public sealed record UserRoleAssigned : IntegrationEvent
{
    public required string RoleCode { get; init; }
}
