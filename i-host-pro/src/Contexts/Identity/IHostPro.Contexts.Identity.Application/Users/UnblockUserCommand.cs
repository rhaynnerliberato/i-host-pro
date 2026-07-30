using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Unblocks a user (Incremento 3, Checkpoint 7). Mirrors
/// <see cref="BlockUserCommand"/>'s shape and non-client-supplied-actor
/// reasoning exactly.
/// </summary>
public sealed record UnblockUserCommand(Guid TenantId, Guid ActorId, Guid TargetUserId) : ICommand;
