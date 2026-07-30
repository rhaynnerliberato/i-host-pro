using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Changes the authenticated caller's own password (Incremento 3, Checkpoint
/// 9). <see cref="TenantId"/>/<see cref="UserId"/> come exclusively from the
/// authenticated access token's <c>tenant_id</c>/<c>sub</c> claims — a
/// controller builds this from <c>ClaimsPrincipal</c>, never from the request
/// body. <see cref="CurrentPassword"/> must match the user's presently
/// persisted password; <see cref="NewPassword"/> must pass the existing
/// password policy and differ from <see cref="CurrentPassword"/>'s underlying
/// value. Revokes every active session of the caller, including the one that
/// originated this request — the caller must authenticate again afterward
/// (Section 9 of the Checkpoint 9 decision).
/// </summary>
public sealed record ChangeOwnPasswordCommand(Guid TenantId, Guid UserId, string CurrentPassword, string NewPassword) : ICommand;
