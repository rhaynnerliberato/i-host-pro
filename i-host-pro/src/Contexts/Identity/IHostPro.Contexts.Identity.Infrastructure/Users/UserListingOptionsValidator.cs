using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Users;

public sealed class UserListingOptionsValidator : IValidateOptions<UserListingOptions>
{
    public ValidateOptionsResult Validate(string? name, UserListingOptions options)
    {
        if (options.DefaultPageSize < 1)
        {
            return ValidateOptionsResult.Fail(
                $"{UserListingOptions.SectionName}:{nameof(UserListingOptions.DefaultPageSize)} must be at least 1 (was {options.DefaultPageSize}).");
        }

        if (options.MaxPageSize < 1)
        {
            return ValidateOptionsResult.Fail(
                $"{UserListingOptions.SectionName}:{nameof(UserListingOptions.MaxPageSize)} must be at least 1 (was {options.MaxPageSize}).");
        }

        if (options.DefaultPageSize > options.MaxPageSize)
        {
            return ValidateOptionsResult.Fail(
                $"{UserListingOptions.SectionName}:{nameof(UserListingOptions.DefaultPageSize)} ({options.DefaultPageSize}) " +
                $"cannot exceed {nameof(UserListingOptions.MaxPageSize)} ({options.MaxPageSize}).");
        }

        return ValidateOptionsResult.Success;
    }
}
