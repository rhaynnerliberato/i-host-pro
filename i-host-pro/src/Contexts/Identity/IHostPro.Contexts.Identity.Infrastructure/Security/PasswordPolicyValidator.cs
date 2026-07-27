using IHostPro.Contexts.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Enforces password policy as configuration-driven Infrastructure, not a
/// Domain invariant — the <see cref="User"/> aggregate never knows about
/// minimum length, character-class requirements or Argon2id parameters
/// (Incremento 1 plan, "Política de senha").
/// </summary>
public sealed class PasswordPolicyValidator : IPasswordValidator<User>
{
    private readonly PasswordPolicyOptions _options;

    public PasswordPolicyValidator(IOptions<PasswordPolicyOptions> options)
    {
        _options = options.Value;
    }

    public Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user, string? password)
    {
        var errors = new List<IdentityError>();

        if (string.IsNullOrEmpty(password) || password.Length < _options.MinimumLength)
        {
            errors.Add(new IdentityError
            {
                Code = "PasswordTooShort",
                Description = $"Password must be at least {_options.MinimumLength} characters long.",
            });
        }

        if (_options.RequireDigit && (password is null || !password.Any(char.IsDigit)))
        {
            errors.Add(new IdentityError { Code = "PasswordRequiresDigit", Description = "Password must contain a digit." });
        }

        if (_options.RequireUppercase && (password is null || !password.Any(char.IsUpper)))
        {
            errors.Add(new IdentityError { Code = "PasswordRequiresUpper", Description = "Password must contain an uppercase letter." });
        }

        if (_options.RequireLowercase && (password is null || !password.Any(char.IsLower)))
        {
            errors.Add(new IdentityError { Code = "PasswordRequiresLower", Description = "Password must contain a lowercase letter." });
        }

        if (_options.RequireSpecialCharacter && (password is null || password.All(char.IsLetterOrDigit)))
        {
            errors.Add(new IdentityError { Code = "PasswordRequiresSpecial", Description = "Password must contain a special character." });
        }

        return Task.FromResult(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed(errors.ToArray()));
    }
}
