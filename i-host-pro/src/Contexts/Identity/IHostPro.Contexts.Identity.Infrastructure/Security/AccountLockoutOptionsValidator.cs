using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

public sealed class AccountLockoutOptionsValidator : IValidateOptions<AccountLockoutOptions>
{
    private const int MinFailedAccessAttempts = 1;
    private const int MaxFailedAccessAttempts = 20; // above this, lockout stops being a meaningful brute-force defense
    private static readonly TimeSpan MinLockoutDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxLockoutDuration = TimeSpan.FromHours(24);

    public ValidateOptionsResult Validate(string? name, AccountLockoutOptions options)
    {
        var failures = new List<string>();

        if (options.MaxFailedAccessAttempts is < MinFailedAccessAttempts or > MaxFailedAccessAttempts)
        {
            failures.Add(
                $"{AccountLockoutOptions.SectionName}:{nameof(AccountLockoutOptions.MaxFailedAccessAttempts)} must be " +
                $"between {MinFailedAccessAttempts} and {MaxFailedAccessAttempts} (was {options.MaxFailedAccessAttempts}).");
        }

        if (options.DefaultLockoutDuration < MinLockoutDuration || options.DefaultLockoutDuration > MaxLockoutDuration)
        {
            failures.Add(
                $"{AccountLockoutOptions.SectionName}:{nameof(AccountLockoutOptions.DefaultLockoutDuration)} must be " +
                $"between {MinLockoutDuration} and {MaxLockoutDuration} (was {options.DefaultLockoutDuration}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
