using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeUserAdministrationReader : IUserAdministrationReader
{
    private readonly PagedResult<UserResult>? _pagedResult;
    private readonly UserResult? _singleResult;

    private FakeUserAdministrationReader(PagedResult<UserResult>? pagedResult, UserResult? singleResult)
    {
        _pagedResult = pagedResult;
        _singleResult = singleResult;
    }

    public static FakeUserAdministrationReader WithPage(PagedResult<UserResult> pagedResult) => new(pagedResult, null);

    public static FakeUserAdministrationReader WithUser(UserResult? user) => new(null, user);

    public int? LastPage { get; private set; }
    public int? LastPageSize { get; private set; }
    public string? LastSearch { get; private set; }
    public UserStatus? LastStatus { get; private set; }
    public Guid? LastUserId { get; private set; }
    public CancellationToken? LastCancellationToken { get; private set; }

    public Task<PagedResult<UserResult>> ListAsync(
        int page, int pageSize, string? search, UserStatus? status, CancellationToken cancellationToken)
    {
        LastPage = page;
        LastPageSize = pageSize;
        LastSearch = search;
        LastStatus = status;
        LastCancellationToken = cancellationToken;
        return Task.FromResult(_pagedResult!);
    }

    public Task<UserResult?> GetByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        LastUserId = userId;
        LastCancellationToken = cancellationToken;
        return Task.FromResult(_singleResult);
    }
}
