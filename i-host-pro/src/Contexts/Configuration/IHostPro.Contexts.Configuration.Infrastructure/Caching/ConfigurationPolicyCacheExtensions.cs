using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.Contexts.Configuration.Infrastructure.Caching;

/// <summary>
/// Registers the Redis-backed <see cref="IPolicyValueCache"/> (Fase 5,
/// Incremento 1, Checkpoint 6) — mirrors <c>IdentitySessionRevocationCacheExtensions</c>'s
/// registration shape, but called from BOTH <c>IHostPro.Api</c>'s composition
/// root (the read path, via <see cref="Resolution.CachedPolicyValueResolver"/>,
/// through <c>AddConfigurationModule</c>) and <c>IHostPro.Worker</c>'s (the
/// invalidation path, via the <c>PolicyUpdated</c> consumer) — unlike session
/// revocation, both hosts genuinely need the real cache, so there is no
/// <c>NullPolicyValueCache</c> default to override here.
/// </summary>
public static class ConfigurationPolicyCacheExtensions
{
    public static IServiceCollection AddConfigurationPolicyCache(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PolicyCacheOptions>()
            .Bind(configuration.GetSection(PolicyCacheOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PolicyCacheOptions>, PolicyCacheOptionsValidator>();

        // AbortOnConnectFail = false: Connect() must never throw even if Redis
        // is completely unreachable at startup — the multiplexer keeps
        // retrying in the background, consistent with "a cache outage
        // degrades to PostgreSQL" applying to startup too, not only to
        // individual operations (mirrors RedisSessionRevocationCache's own
        // precedent exactly).
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PolicyCacheOptions>>().Value;
            var configurationOptions = ConfigurationOptions.Parse(options.ConnectionString);
            configurationOptions.AbortOnConnectFail = false;
            configurationOptions.ConnectTimeout = (int)options.ConnectTimeout.TotalMilliseconds;
            configurationOptions.SyncTimeout = (int)options.OperationTimeout.TotalMilliseconds;
            configurationOptions.AsyncTimeout = (int)options.OperationTimeout.TotalMilliseconds;
            configurationOptions.ConnectRetry = options.ConnectRetry;

            return ConnectionMultiplexer.Connect(configurationOptions);
        });

        services.AddScoped<IPolicyValueCache, RedisPolicyValueCache>();
        services.AddScoped<IPolicyCacheInvalidator, RedisPolicyValueCache>();

        return services;
    }
}
