using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// Single composition-root entry point for the External Integrations module
/// (Fase 9, Checkpoint 2.1) — mirrors <c>CommunicationModuleExtensions</c>
/// exactly.
/// </summary>
public static class ExternalIntegrationsModuleExtensions
{
    /// <param name="isDevelopmentEnvironment">
    /// Whether the calling host is running in the Development environment.
    /// Passed explicitly (rather than resolving <c>IHostEnvironment</c> inside
    /// this method) to avoid adding a hosting-abstractions dependency to this
    /// class library — mirrors <c>AddIdentityModule</c>'s own precedent
    /// exactly. Gates registration of <see cref="DevelopmentWhatsAppCredentialProvider"/>
    /// only — the DbContext/schema is registered unconditionally in every
    /// environment (CP2.1 mandate §41: schema/config is not an external
    /// side-effect, unlike the fake connector/listener/topology gates of
    /// Checkpoint 1).
    /// </param>
    public static IServiceCollection AddExternalIntegrationsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopmentEnvironment)
    {
        services.AddDbContext<ExternalIntegrationsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("ExternalIntegrations"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations")));

        services.AddSingleton(TimeProvider.System);

        // Development-only (CP2.1 mandate §12): no Production backend exists
        // yet — resolving IWhatsAppCredentialProvider outside Development
        // must fail loudly (no registration), never silently fall back to
        // this one.
        if (isDevelopmentEnvironment)
            services.AddScoped<IWhatsAppCredentialProvider, DevelopmentWhatsAppCredentialProvider>();

        return services;
    }
}
