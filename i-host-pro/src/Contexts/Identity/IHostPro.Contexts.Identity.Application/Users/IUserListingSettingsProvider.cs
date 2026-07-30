namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Read-only accessor Application-layer code uses to read the configured
/// user-listing bounds without depending on <c>IOptions&lt;UserListingOptions&gt;</c>
/// directly (Application cannot reference <c>Microsoft.Extensions.Options</c>
/// — mirrors exactly why <c>ICurrentTenantProvider</c> exists for
/// <c>ITenantContext</c>, Incremento 2 plan, Etapa 9).
/// </summary>
public interface IUserListingSettingsProvider
{
    int DefaultPageSize { get; }

    int MaxPageSize { get; }
}
