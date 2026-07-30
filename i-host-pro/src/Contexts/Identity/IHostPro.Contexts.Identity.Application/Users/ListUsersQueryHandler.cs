using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Users;

public sealed class ListUsersQueryHandler : IQueryHandler<ListUsersQuery, PagedResult<UserResult>>
{
    private readonly IUserAdministrationReader _reader;
    private readonly IUserListingSettingsProvider _settings;

    public ListUsersQueryHandler(IUserAdministrationReader reader, IUserListingSettingsProvider settings)
    {
        _reader = reader;
        _settings = settings;
    }

    public async ValueTask<Result<PagedResult<UserResult>>> Handle(ListUsersQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? _settings.DefaultPageSize;

        var result = await _reader.ListAsync(page, pageSize, query.Search, query.Status, cancellationToken);

        return Result.Success(result);
    }
}
