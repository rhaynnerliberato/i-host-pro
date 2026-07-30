using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Removes a platform role from a user (Incremento 3, Checkpoint 6). Mirrors
/// <see cref="AssignRoleCommand"/>'s shape and non-client-supplied-actor
/// reasoning exactly. <see cref="RoleCode"/> is the <c>{roleCode}</c> route
/// segment on <c>DELETE /api/v1/users/{userId}/roles/{roleCode}</c> — there
/// is no request body for this action.
/// </summary>
public sealed record RemoveRoleCommand(Guid TenantId, Guid ActorId, Guid TargetUserId, string RoleCode) : ICommand;
