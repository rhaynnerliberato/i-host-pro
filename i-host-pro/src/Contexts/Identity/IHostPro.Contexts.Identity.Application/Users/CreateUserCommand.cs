using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Creates a new user within the caller's tenant, with exactly one mandatory
/// initial role (Incremento 3, Checkpoint 5). <see cref="TenantId"/>/
/// <see cref="ActorId"/> come exclusively from the authenticated
/// Administrator's access token claims — a controller builds this from
/// <c>AuthenticatedIdentityReader</c>, never from the request body; there is
/// no way for a client to create a user in another tenant or attribute the
/// action to a different actor. <see cref="RoleCode"/> is a single string,
/// never a collection — "não aceitar lista de papéis" (Section 2).
///
/// Not an <c>IBootstrapRequest</c>: the caller is already authenticated, so
/// the tenant is resolved the normal way, from the JWT claim, via
/// <c>CreateUserTenantAwareBehavior</c>.
/// </summary>
public sealed record CreateUserCommand(
    Guid TenantId,
    Guid ActorId,
    string FullName,
    string Email,
    string InitialPassword,
    string RoleCode) : ICommand<UserResult>;
