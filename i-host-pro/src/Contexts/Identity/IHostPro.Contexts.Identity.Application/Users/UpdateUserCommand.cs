using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Updates a user's <see cref="FullName"/> and/or <see cref="Email"/>
/// (Incremento 3, Checkpoint 8). <see cref="TenantId"/>/<see cref="ActorId"/>
/// come exclusively from the authenticated Administrator's access token
/// claims — a controller builds this from <c>ClaimsPrincipal</c>, never from
/// the request body. <see cref="TargetUserId"/> is the <c>{userId:guid}</c>
/// route parameter.
///
/// <see cref="FullName"/>/<see cref="Email"/> are each independently
/// optional — <c>null</c> means "not supplied, leave unchanged", distinct
/// from an empty/whitespace string (supplied but invalid, rejected). At
/// least one of the two must be non-null (Section 3).
/// </summary>
public sealed record UpdateUserCommand(Guid TenantId, Guid ActorId, Guid TargetUserId, string? FullName, string? Email) : ICommand<UserResult>;
