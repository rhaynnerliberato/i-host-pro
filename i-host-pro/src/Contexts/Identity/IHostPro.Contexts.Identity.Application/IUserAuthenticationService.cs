using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Exactly the ASP.NET Core Identity <c>UserManager&lt;User&gt;</c> operations
/// <c>LoginCommandHandler</c> needs (Incremento 2 plan, Etapa 9) — introduced
/// after <c>IHostPro.ArchitectureTests.IdentityDependencyTests.
/// Application_Should_Not_Depend_On_Infrastructure_Or_AspNetCoreIdentity</c>
/// (an Incremento 1 rule) caught the handler depending on
/// <c>UserManager&lt;User&gt;</c> directly. The Infrastructure implementation
/// is a thin, behavior-preserving delegation to the real
/// <c>UserManager&lt;User&gt;</c> — no logic duplicated or changed, only the
/// dependency direction corrected: Application depends on this abstraction,
/// never on <c>Microsoft.AspNetCore.Identity</c> itself.
/// </summary>
public interface IUserAuthenticationService
{
    Task<User?> FindByEmailAsync(string email);

    /// <summary>Looks up a user by primary key (Incremento 2 plan, Etapa 10 — refresh token exchange has no e-mail to look up by).</summary>
    Task<User?> FindByIdAsync(Guid userId);

    Task<bool> IsLockedOutAsync(User user);

    Task<bool> CheckPasswordAsync(User user, string password);

    /// <summary>Increments the failed-access counter and, once it reaches the configured threshold, locks the account.</summary>
    Task AccessFailedAsync(User user);

    Task ResetAccessFailedCountAsync(User user);
}
