using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Profile;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeUserRoleReader : IUserRoleReader
{
    private readonly IReadOnlyCollection<string> _roleCodes;

    private FakeUserRoleReader(IReadOnlyCollection<string> roleCodes) => _roleCodes = roleCodes;

    public static FakeUserRoleReader WithRoleCodes(params string[] roleCodes) => new(roleCodes);

    public int CallCount { get; private set; }
    public CancellationToken? LastCancellationToken { get; private set; }

    public Task<IReadOnlyCollection<string>> GetRoleCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        CallCount++;
        LastCancellationToken = cancellationToken;
        return Task.FromResult(_roleCodes);
    }

    // Unused by GetOwnProfileQueryHandlerTests (the only consumer of this
    // fake) — added only to satisfy IUserRoleReader (Incremento 3, Checkpoint
    // 6 addition); AssignRole/RemoveRole handler tests use their own
    // purpose-built fake instead (see Application/Users/FakeUserRoleReader.cs).
    public Task<UserRole?> FindAsync(Guid userId, string roleCode, CancellationToken cancellationToken) =>
        Task.FromResult<UserRole?>(null);
}
