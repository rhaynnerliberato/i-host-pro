using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Authorization;

public sealed class PermissionCacheOptionsValidator : IValidateOptions<PermissionCacheOptions>
{
    private static readonly TimeSpan MinLifetime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    public ValidateOptionsResult Validate(string? name, PermissionCacheOptions options)
    {
        if (options.Lifetime < MinLifetime || options.Lifetime > MaxLifetime)
        {
            return ValidateOptionsResult.Fail(
                $"{PermissionCacheOptions.SectionName}:{nameof(PermissionCacheOptions.Lifetime)} must be " +
                $"between {MinLifetime} and {MaxLifetime} (was {options.Lifetime}).");
        }

        return ValidateOptionsResult.Success;
    }
}
