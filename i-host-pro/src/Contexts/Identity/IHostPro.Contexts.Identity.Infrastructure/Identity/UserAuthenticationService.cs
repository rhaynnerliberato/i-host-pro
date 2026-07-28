using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.AspNetCore.Identity;

namespace IHostPro.Contexts.Identity.Infrastructure.Identity;

/// <inheritdoc cref="IUserAuthenticationService"/>
/// <remarks>
/// Thin, behavior-preserving delegation to <see cref="UserManager{TUser}"/> —
/// no logic duplicated or changed relative to calling <c>UserManager</c>
/// directly. Exists solely so <c>LoginCommandHandler</c> (Application layer)
/// never references <c>Microsoft.AspNetCore.Identity</c>, per
/// <c>IdentityDependencyTests.Application_Should_Not_Depend_On_Infrastructure_Or_AspNetCoreIdentity</c>
/// (Incremento 1 rule).
/// </remarks>
public sealed class UserAuthenticationService : IUserAuthenticationService
{
    private readonly UserManager<User> _userManager;

    public UserAuthenticationService(UserManager<User> userManager) => _userManager = userManager;

    public Task<User?> FindByEmailAsync(string email) => _userManager.FindByEmailAsync(email);

    public Task<User?> FindByIdAsync(Guid userId) => _userManager.FindByIdAsync(userId.ToString());

    public Task<bool> IsLockedOutAsync(User user) => _userManager.IsLockedOutAsync(user);

    public Task<bool> CheckPasswordAsync(User user, string password) => _userManager.CheckPasswordAsync(user, password);

    public Task AccessFailedAsync(User user) => _userManager.AccessFailedAsync(user);

    public Task ResetAccessFailedCountAsync(User user) => _userManager.ResetAccessFailedCountAsync(user);
}
