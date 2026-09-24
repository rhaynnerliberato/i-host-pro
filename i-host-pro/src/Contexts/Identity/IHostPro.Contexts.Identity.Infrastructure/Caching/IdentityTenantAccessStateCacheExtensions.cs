using IHostPro.Contexts.Identity.Application;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace IHostPro.Contexts.Identity.Infrastructure.Caching;

/// <summary>
/// Registers the Redis-backed <see cref="ITenantAccessStateCache"/> (Tenant
/// Suspension/Reactivation Enforcement workstream) — deliberately kept out of
/// <see cref="IdentityModuleExtensions.AddIdentityModule"/> and registered
/// ONLY here, called EXCLUSIVELY from <c>IHostPro.Api</c>'s composition root,
/// never from <c>IHostPro.Worker</c>'s, mirroring
/// <c>IdentitySessionRevocationCacheExtensions</c>. <c>AddIdentityModule</c>
/// already registers a harmless <see cref="NullTenantAccessStateCache"/> for
/// both hosts — this overrides it (DI resolves the last registration) with
/// the real Redis-backed implementation, Api-only.
///
/// Must be called after <c>AddIdentitySessionRevocationCache</c>, which owns
/// the shared <see cref="IConnectionMultiplexer"/> registration this reuses —
/// both caches live on the same Redis instance, so this deliberately does not
/// open a second connection.
/// </summary>
public static class IdentityTenantAccessStateCacheExtensions
{
    public static IServiceCollection AddIdentityTenantAccessStateCache(this IServiceCollection services)
    {
        services.AddScoped<ITenantAccessStateCache, RedisTenantAccessStateCache>();

        return services;
    }
}
