using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;
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

        // Repositories — unconditional in every environment, mirroring the
        // DbContext registration above (Fase 9, Checkpoint 2.2): every call
        // site (IHostPro.Api's command dispatch, MetaWhatsAppMessagingProvider)
        // needs both, without depending on AddExternalIntegrationsCommandDispatch's
        // Mediator-specific wiring.
        services.AddScoped<IWhatsAppIntegrationRepository, WhatsAppIntegrationRepository>();
        services.AddScoped<IWhatsAppTemplateMappingRepository, WhatsAppTemplateMappingRepository>();

        // Fase 9, Checkpoint 2.3.2 — global (non-tenant-owned) routing
        // directory. Unconditional like the repositories above: no secret,
        // no external network call, just a plain table lookup.
        services.AddScoped<IWhatsAppTenantRouteRepository, WhatsAppTenantRouteRepository>();
        services.AddScoped<IWhatsAppTenantRouteResolver, WhatsAppTenantRouteResolver>();

        // Development-only (CP2.1 mandate §12): no Production backend exists
        // yet — resolving IWhatsAppCredentialProvider outside Development
        // must fail loudly (no registration), never silently fall back to
        // this one.
        if (isDevelopmentEnvironment)
            services.AddScoped<IWhatsAppCredentialProvider, DevelopmentWhatsAppCredentialProvider>();

        // Fase 9, Checkpoint 2.3.1 — webhook security ingress (ADR-022).
        // Same Development-only rationale as IWhatsAppCredentialProvider
        // above, but a deliberately separate, app/deployment-level
        // abstraction (ADR-022 item 8/9): the webhook must verify its caller
        // before any TenantId is known, so it can never resolve credentials
        // via the tenant-owned WhatsAppIntegration/IWhatsAppCredentialProvider
        // path.
        if (isDevelopmentEnvironment)
            services.AddScoped<IWhatsAppWebhookCredentialProvider, DevelopmentWhatsAppWebhookCredentialProvider>();

        // Unconditional in every environment (unlike the credential provider
        // above): this is a stateless verification algorithm with no secret/
        // network dependency of its own — only the credential SOURCE is
        // Production-blocked, never the algorithm that consumes it.
        services.AddSingleton<IWebhookSignatureVerifier, MetaWebhookSignatureVerifier>();

        // Fase 9, Checkpoint 2.3.2 — webhook status normalization
        // (ADR-022). Unconditional: no secret, no external network call —
        // just JSON parsing plus the route repository above.
        services.AddScoped<IWhatsAppWebhookStatusProcessor, MetaWebhookStatusProcessor>();

        // Fase 9, Checkpoint 2.2 — real Meta Cloud API outbound connector.
        // Development-only, same rationale as IWhatsAppCredentialProvider
        // above: MetaWhatsAppMessagingProvider depends on it, so gating both
        // to Development is belt-and-suspenders (resolving IMessagingProvider
        // outside Development already fails transitively — mandate §10: no
        // fallback to Development credentials in any other environment).
        //
        // Registered here so it is resolvable directly (e.g. by a dedicated
        // sandbox-proof test) without being wired into Communication's
        // automatic ReservationCreated flow — that flow keeps using
        // FakeWhatsAppConnector this checkpoint (mandate §46-49, Option A:
        // WhatsAppIntegration.IsEnabled stays false/unchanged, so the real
        // flow is accessible only via a direct provider/Application call).
        if (isDevelopmentEnvironment)
        {
            services.Configure<MetaWhatsAppOptions>(configuration.GetSection("ExternalIntegrations:WhatsApp:Meta"));

            services.AddHttpClient(MetaWhatsAppMessagingProvider.HttpClientName, (serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MetaWhatsAppOptions>>().Value;
                client.BaseAddress = new Uri("https://graph.facebook.com/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });

            services.AddScoped<IMessagingProvider, MetaWhatsAppMessagingProvider>();
        }

        return services;
    }
}
