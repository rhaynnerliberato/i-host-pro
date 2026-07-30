using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Profile;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeUserAuthenticationService : IUserAuthenticationService
{
    private readonly User? _user;

    private FakeUserAuthenticationService(User? user) => _user = user;

    public static FakeUserAuthenticationService WithUser(User user) => new(user);

    public static FakeUserAuthenticationService WithNoUser() => new(null);

    public Task<User?> FindByIdAsync(Guid userId) =>
        Task.FromResult(_user is not null && _user.Id == userId ? _user : null);

    public Task<User?> FindByEmailAsync(string email) => throw new NotSupportedException();

    public Task<bool> IsLockedOutAsync(User user) => throw new NotSupportedException();

    public Task<bool> CheckPasswordAsync(User user, string password) => throw new NotSupportedException();

    public Task AccessFailedAsync(User user) => throw new NotSupportedException();

    public Task ResetAccessFailedCountAsync(User user) => throw new NotSupportedException();
}
