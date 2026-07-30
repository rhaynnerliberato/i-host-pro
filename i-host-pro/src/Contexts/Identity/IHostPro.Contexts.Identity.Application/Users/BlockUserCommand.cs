using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Blocks a user (Incremento 3, Checkpoint 7). <see cref="TenantId"/>/
/// <see cref="ActorId"/> come exclusively from the authenticated
/// Administrator's access token claims — a controller builds this from
/// <c>ClaimsPrincipal</c>, never from the request body.
/// <see cref="TargetUserId"/> is the <c>{userId:guid}</c> route parameter.
/// Blocking the acting Administrator's own account is a valid input — it is
/// only rejected if it would leave the tenant without another active
/// Administrator (<see cref="ILastAdministratorGuard"/>), the same as any
/// other target.
/// </summary>
public sealed record BlockUserCommand(Guid TenantId, Guid ActorId, Guid TargetUserId) : ICommand;
