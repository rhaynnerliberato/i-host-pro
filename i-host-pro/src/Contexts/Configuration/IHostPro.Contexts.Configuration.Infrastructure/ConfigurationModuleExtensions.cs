using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Infrastructure.Caching;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Infrastructure.Resolution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Configuration.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Configuration &amp; Policy
/// module — mirrors <c>ReservationsModuleExtensions</c> exactly. The Host
/// (IHostPro.Api) calls this once.
/// </summary>
public static class ConfigurationModuleExtensions
{
    private const string UncachedResolverKey = "uncached";

    public static IServiceCollection AddConfigurationModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — mirrors
        // ReservationsModuleExtensions/PropertyManagementModuleExtensions.
        services.AddDbContext<ConfigurationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Configuration"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration")));

        services.AddSingleton(TimeProvider.System);

        // The generic resolver stays internal to this assembly (Fase 5,
        // Incremento 1 official decisions §5) — only the two policy-specific
        // readers below are registered against their public Contracts
        // interfaces. IPolicyValueResolver resolves to the caching decorator
        // (Checkpoint 6); the real, DB-only PolicyValueResolver is registered
        // under the same IPolicyValueResolver interface but keyed, so
        // CachedPolicyValueResolver can depend on the interface (unit-testable
        // with a fake) rather than the concrete type, with no DI cycle.
        services.AddKeyedScoped<IPolicyValueResolver, PolicyValueResolver>(UncachedResolverKey);
        services.AddScoped<IPolicyValueResolver>(sp => new CachedPolicyValueResolver(
            sp.GetRequiredKeyedService<IPolicyValueResolver>(UncachedResolverKey),
            sp.GetRequiredService<IPolicyValueCache>()));
        services.AddScoped<IEarlyCheckInPolicyReader, EarlyCheckInPolicyReader>();
        services.AddScoped<ILateCheckoutPolicyReader, LateCheckoutPolicyReader>();

        services.AddConfigurationPolicyCache(configuration);

        return services;
    }
}
