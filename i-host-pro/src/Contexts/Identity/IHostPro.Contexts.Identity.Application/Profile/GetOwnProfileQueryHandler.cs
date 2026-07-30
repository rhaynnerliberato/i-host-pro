using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;

namespace IHostPro.Contexts.Identity.Application.Profile;

/// <summary>
/// Reuses <see cref="IUserAuthenticationService.FindByIdAsync"/> (the same
/// lookup <c>RefreshTokenCommandHandler</c> already uses) rather than
/// introducing a new reader for what is already a simple, existing
/// by-primary-key lookup — <see cref="Domain.User"/> has no generic
/// <c>IRepository&lt;User,Guid&gt;</c> registration in this codebase, it is
/// managed exclusively through ASP.NET Core Identity's
/// <c>UserManager&lt;User&gt;</c>/<c>UserStore</c> (Incremento 1 plan,
/// Section 2), for which <see cref="IUserAuthenticationService"/> is the
/// Application-layer abstraction. Roles come from <see cref="IUserRoleReader"/>,
/// the same reader Login/Refresh already use, sorted here with
/// <see cref="StringComparer.Ordinal"/> — the reader itself returns them in
/// whatever order the underlying query produces, never guaranteed sorted.
/// </summary>
public sealed class GetOwnProfileQueryHandler : IQueryHandler<GetOwnProfileQuery, OwnProfileResult>
{
    private static readonly Error UserNotFoundError = new(
        IdentityErrorCodes.AuthenticatedUserNotFound, IdentityErrorCodes.AuthenticatedUserNotFound);

    private readonly IUserAuthenticationService _userAuthenticationService;
    private readonly IUserRoleReader _roleReader;

    public GetOwnProfileQueryHandler(IUserAuthenticationService userAuthenticationService, IUserRoleReader roleReader)
    {
        _userAuthenticationService = userAuthenticationService;
        _roleReader = roleReader;
    }

    public async ValueTask<Result<OwnProfileResult>> Handle(GetOwnProfileQuery query, CancellationToken cancellationToken)
    {
        var user = await _userAuthenticationService.FindByIdAsync(query.UserId);
        if (user is null)
            return Result.Failure<OwnProfileResult>(UserNotFoundError);

        var roles = await _roleReader.GetRoleCodesAsync(user.Id, cancellationToken);
        var sortedRoles = roles.OrderBy(code => code, StringComparer.Ordinal).ToArray();

        var result = new OwnProfileResult(
            user.Id, user.FullName, user.Email.Value, user.Status.ToString(), sortedRoles, user.CreatedAt, user.LastLoginAt);

        return Result.Success(result);
    }
}
