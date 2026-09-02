using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.BuildingBlocks.Infrastructure.RateLimiting;

/// <summary>
/// Registers <see cref="IDistributedRateLimiter"/> — called from BOTH
/// <c>IHostPro.Api</c>'s composition root (HTTP policies, via a custom
/// <c>PartitionedRateLimiter</c> wrapping this service) and
/// <c>IHostPro.Worker</c>'s (the AI cost-guard policy, called directly from
/// the Wolverine handler) — mirrors <c>AddConfigurationPolicyCache</c>'s own
/// registration shape, with its own independent <see cref="IConnectionMultiplexer"/>
/// (never Configuration &amp; Policy's).
/// </summary>
public static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddIHostProRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitingOptions>, RateLimitingOptionsValidator>();

        // AbortOnConnectFail = false: every policy has an explicit fail-open/
        // fail-closed behavior for an unreachable Redis (see
        // RateLimitFailureMode) — the host must still start even if Redis is
        // down at boot, mirroring RedisPolicyValueCache's own precedent.
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
            var configurationOptions = ConfigurationOptions.Parse(options.Redis.ConnectionString);
            configurationOptions.AbortOnConnectFail = false;
            configurationOptions.ConnectTimeout = (int)options.Redis.ConnectTimeout.TotalMilliseconds;
            configurationOptions.SyncTimeout = (int)options.Redis.OperationTimeout.TotalMilliseconds;
            configurationOptions.AsyncTimeout = (int)options.Redis.OperationTimeout.TotalMilliseconds;
            configurationOptions.ConnectRetry = options.Redis.ConnectRetry;

            return ConnectionMultiplexer.Connect(configurationOptions);
        });

        services.AddSingleton<IDistributedRateLimiter, RedisFixedWindowRateLimiter>();

        return services;
    }
}
