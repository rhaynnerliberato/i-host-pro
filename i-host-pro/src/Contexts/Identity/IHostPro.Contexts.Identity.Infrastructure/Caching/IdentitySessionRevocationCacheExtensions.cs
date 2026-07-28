using IHostPro.Contexts.Identity.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.Contexts.Identity.Infrastructure.Caching;

/// <summary>
/// Registers the Redis-backed <see cref="ISessionRevocationCache"/>
/// (Incremento 2 plan, Etapa 12) — deliberately kept out of
/// <see cref="IdentityModuleExtensions.AddIdentityModule"/> (mirroring
/// <c>AddIdentityJwtIssuance</c>, Etapa 6) and registered ONLY here, called
/// EXCLUSIVELY from <c>IHostPro.Api</c>'s composition root, never from
/// <c>IHostPro.Worker</c>'s. <c>AddIdentityModule</c> already registers a
/// harmless no-op <see cref="NullSessionRevocationCache"/> for both hosts —
/// this overrides it (DI resolves the last registration) with the real
/// Redis-backed implementation, Api-only.
/// </summary>
public static class IdentitySessionRevocationCacheExtensions
{
    public static IServiceCollection AddIdentitySessionRevocationCache(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionRevocationCacheOptions>()
            .Bind(configuration.GetSection(SessionRevocationCacheOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SessionRevocationCacheOptions>, SessionRevocationCacheOptionsValidator>();

        // AbortOnConnectFail = false: Connect() must never throw even if
        // Redis is completely unreachable at startup — the multiplexer keeps
        // retrying in the background, consistent with "Redis is never the
        // source of truth" (Incremento 2 plan, Etapa 12) applying to startup
        // too, not only to individual operations.
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SessionRevocationCacheOptions>>().Value;
            var configurationOptions = ConfigurationOptions.Parse(options.ConnectionString);
            configurationOptions.AbortOnConnectFail = false;

            // Confirmed empirically (Incremento 2 plan, homologação real)
            // that StackExchange.Redis's own defaults (5000ms/5000ms/5000ms,
            // ConnectRetry=3) made every operation — including
            // IsRevokedAsync, called on every authenticated request via JWT
            // Bearer validation, not just Logout — block for around 11
            // seconds while Redis was unreachable before the fail-open catch
            // in RedisSessionRevocationCache took effect. See
            // SessionRevocationCacheOptions's doc comment.
            configurationOptions.ConnectTimeout = (int)options.ConnectTimeout.TotalMilliseconds;
            configurationOptions.SyncTimeout = (int)options.OperationTimeout.TotalMilliseconds;
            configurationOptions.AsyncTimeout = (int)options.OperationTimeout.TotalMilliseconds;
            configurationOptions.ConnectRetry = options.ConnectRetry;

            return ConnectionMultiplexer.Connect(configurationOptions);
        });

        services.AddScoped<ISessionRevocationCache, RedisSessionRevocationCache>();

        return services;
    }
}
