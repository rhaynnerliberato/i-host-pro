using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// The two password-provisioning operations <c>CreateUserCommandHandler</c>
/// needs (Incremento 3, Checkpoint 5) — introduced for the same reason as
/// <see cref="IUserAuthenticationService"/> (Etapa 9): Application must never
/// depend on <c>Microsoft.AspNetCore.Identity</c> directly. The Infrastructure
/// implementation calls <c>IPasswordValidator&lt;User&gt;</c>/
/// <c>IPasswordHasher&lt;User&gt;</c> exactly the way <c>DevelopmentIdentitySeeder</c>
/// already does (manager/user arguments passed as <c>null!</c> — an
/// established, approved convention in this codebase for a user that does
/// not exist yet) — never a parallel, hand-written implementation of either
/// concern.
/// </summary>
public interface IUserProvisioningService
{
    Task<PasswordValidationResult> ValidatePasswordAsync(string password);

    PasswordHash HashPassword(string password);
}
