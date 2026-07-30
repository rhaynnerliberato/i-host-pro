using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>
/// Hand-written test double — this project uses no mocking library, consistent
/// with the rest of the solution. Distinct from
/// <c>Application.Profile.FakeUserRoleReader</c> (used only by
/// <c>GetOwnProfileQueryHandlerTests</c>): this one also configures
/// <see cref="FindAsync"/>, which AssignRole/RemoveRole handler tests need.
/// </summary>
internal sealed class FakeUserRoleReader : IUserRoleReader
{
    private readonly IReadOnlyCollection<string> _roleCodes;
    private readonly UserRole? _foundUserRole;

    private FakeUserRoleReader(IReadOnlyCollection<string> roleCodes, UserRole? foundUserRole)
    {
        _roleCodes = roleCodes;
        _foundUserRole = foundUserRole;
    }

    public static FakeUserRoleReader WithRoleCodes(params string[] roleCodes) => new(roleCodes, foundUserRole: null);

    public static FakeUserRoleReader WithRoleCodesAndFindResult(string[] roleCodes, UserRole? foundUserRole) =>
        new(roleCodes, foundUserRole);

    public int GetRoleCodesCallCount { get; private set; }
    public int FindCallCount { get; private set; }
    public string? LastFindRoleCode { get; private set; }

    public Task<IReadOnlyCollection<string>> GetRoleCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        GetRoleCodesCallCount++;
        return Task.FromResult(_roleCodes);
    }

    public Task<UserRole?> FindAsync(Guid userId, string roleCode, CancellationToken cancellationToken)
    {
        FindCallCount++;
        LastFindRoleCode = roleCode;
        return Task.FromResult(_foundUserRole);
    }
}
