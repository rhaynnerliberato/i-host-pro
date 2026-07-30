using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>
/// Hand-written test double — this project uses no mocking library, consistent
/// with the rest of the solution. Only <see cref="CheckPasswordAsync"/> is
/// exercised by <c>ChangeOwnPasswordCommandHandlerTests</c>/
/// <c>AdminResetPasswordCommandHandlerTests</c> — every other member throws,
/// mirroring <c>Profile.FakeUserAuthenticationService</c>'s convention of only
/// implementing what its own tests need.
/// </summary>
internal sealed class FakeUserAuthenticationServiceForPasswordChecks : IUserAuthenticationService
{
    private readonly string _currentPassword;

    private FakeUserAuthenticationServiceForPasswordChecks(string currentPassword) => _currentPassword = currentPassword;

    public static FakeUserAuthenticationServiceForPasswordChecks WithCurrentPassword(string currentPassword) => new(currentPassword);

    public int CheckPasswordCallCount { get; private set; }

    public Task<bool> CheckPasswordAsync(User user, string password)
    {
        CheckPasswordCallCount++;
        return Task.FromResult(string.Equals(password, _currentPassword, StringComparison.Ordinal));
    }

    public Task<User?> FindByEmailAsync(string email) => throw new NotSupportedException();

    public Task<User?> FindByIdAsync(Guid userId) => throw new NotSupportedException();

    public Task<bool> IsLockedOutAsync(User user) => throw new NotSupportedException();

    public Task AccessFailedAsync(User user) => throw new NotSupportedException();

    public Task ResetAccessFailedCountAsync(User user) => throw new NotSupportedException();
}
