using IHostPro.Contexts.Identity.Application.Users;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Users;

/// <inheritdoc cref="IUserListingSettingsProvider"/>
public sealed class UserListingSettingsProvider : IUserListingSettingsProvider
{
    private readonly UserListingOptions _options;

    public UserListingSettingsProvider(IOptions<UserListingOptions> options) => _options = options.Value;

    public int DefaultPageSize => _options.DefaultPageSize;

    public int MaxPageSize => _options.MaxPageSize;
}
