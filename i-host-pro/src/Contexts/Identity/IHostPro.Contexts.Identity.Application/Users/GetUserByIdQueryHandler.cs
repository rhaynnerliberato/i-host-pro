using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;

namespace IHostPro.Contexts.Identity.Application.Users;

public sealed class GetUserByIdQueryHandler : IQueryHandler<GetUserByIdQuery, UserResult>
{
    private static readonly Error UserNotFoundError = new(IdentityErrorCodes.UserNotFound, IdentityErrorCodes.UserNotFound);

    private readonly IUserAdministrationReader _reader;

    public GetUserByIdQueryHandler(IUserAdministrationReader reader) => _reader = reader;

    public async ValueTask<Result<UserResult>> Handle(GetUserByIdQuery query, CancellationToken cancellationToken)
    {
        var user = await _reader.GetByIdAsync(query.UserId, cancellationToken);

        return user is null ? Result.Failure<UserResult>(UserNotFoundError) : Result.Success(user);
    }
}
