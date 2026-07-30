using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using Microsoft.AspNetCore.Identity;

namespace IHostPro.Contexts.Identity.Infrastructure.Identity;

/// <inheritdoc cref="IUserProvisioningService"/>
/// <remarks>
/// Calls <see cref="IPasswordValidator{TUser}"/>/<see cref="IPasswordHasher{TUser}"/>
/// directly — the exact same calls, with the exact same <c>null!</c>
/// manager/user argument convention, as <c>DevelopmentIdentitySeeder</c>
/// already uses for a user that does not exist yet. Deliberately never goes
/// through <c>UserManager&lt;User&gt;</c>: <c>UserManager.CreateAsync</c> would
/// call <c>SaveChangesAsync</c> itself, which would conflict with this
/// codebase's rule that the single <c>SaveChangesAsync</c>/outbox flush for a
/// whole use case belongs exclusively to <c>IIdentityTransactionExecutor</c>.
/// </remarks>
public sealed class UserProvisioningService : IUserProvisioningService
{
    private readonly IPasswordValidator<User> _passwordValidator;
    private readonly IPasswordHasher<User> _passwordHasher;

    public UserProvisioningService(IPasswordValidator<User> passwordValidator, IPasswordHasher<User> passwordHasher)
    {
        _passwordValidator = passwordValidator;
        _passwordHasher = passwordHasher;
    }

    public async Task<PasswordValidationResult> ValidatePasswordAsync(string password)
    {
        var result = await _passwordValidator.ValidateAsync(manager: null!, user: null!, password);

        return result.Succeeded
            ? PasswordValidationResult.Success
            : PasswordValidationResult.Failure(result.Errors.Select(e => e.Code).ToArray());
    }

    public PasswordHash HashPassword(string password) =>
        PasswordHash.FromEncoded(_passwordHasher.HashPassword(null!, password));
}
