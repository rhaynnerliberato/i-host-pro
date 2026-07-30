using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Resets a user's password on their behalf, without requiring their current
/// password (Incremento 3, Checkpoint 9). <see cref="TenantId"/>/<see cref="ActorId"/>
/// come exclusively from the authenticated Administrator's access token
/// claims — a controller builds this from <c>ClaimsPrincipal</c>, never from
/// the request body. <see cref="TargetUserId"/> is the <c>{userId:guid}</c>
/// route parameter. An Administrator may not target their own account with
/// this command (<c>Identity.AdminCannotResetOwnPassword</c>) — the
/// self-service <see cref="ChangeOwnPasswordCommand"/> is the only path for
/// that, since it requires the current password. Resetting a Blocked user's
/// password is a valid input — it never unblocks them (Section 4 of the
/// Checkpoint 9 decision).
/// </summary>
public sealed record AdminResetPasswordCommand(Guid TenantId, Guid ActorId, Guid TargetUserId, string NewPassword) : ICommand;
