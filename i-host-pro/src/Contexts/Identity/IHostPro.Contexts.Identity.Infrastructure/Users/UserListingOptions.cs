namespace IHostPro.Contexts.Identity.Infrastructure.Users;

/// <summary>
/// Bounds for the administrative user listing (Incremento 3, Checkpoint 5) —
/// configuration-driven, matching the established <c>IOptions</c>/
/// <c>ValidateOnStart</c> pattern already used for <c>PermissionCacheOptions</c>/
/// <c>AccountLockoutOptions</c>. Exposed to Application only through
/// <see cref="IUserListingSettingsProvider"/> — this class itself is an
/// Infrastructure/<c>Microsoft.Extensions.Options</c> concern (Application
/// does not reference that package, mirroring why <c>ICurrentTenantProvider</c>
/// exists for <c>ITenantContext</c>).
/// </summary>
public sealed class UserListingOptions
{
    public const string SectionName = "Identity:UserListing";

    public int DefaultPageSize { get; init; } = 20;

    public int MaxPageSize { get; init; } = 100;
}
