using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Assigns a platform role to a user (Incremento 3, Checkpoint 6).
/// <see cref="TenantId"/>/<see cref="ActorId"/> come exclusively from the
/// authenticated Administrator's access token claims — a controller builds
/// this from <c>ClaimsPrincipal</c>, never from the request body.
/// <see cref="TargetUserId"/> is the <c>{userId:guid}</c> route parameter;
/// <see cref="RoleCode"/> is the sole request-body field. No field here may
/// ever be sourced from anything client-supplied besides those two route/body
/// values (Section 1: never <c>tenantId</c>/<c>actorId</c>/<c>assignedBy</c>
/// from the request).
/// </summary>
public sealed record AssignRoleCommand(Guid TenantId, Guid ActorId, Guid TargetUserId, string RoleCode) : ICommand;
