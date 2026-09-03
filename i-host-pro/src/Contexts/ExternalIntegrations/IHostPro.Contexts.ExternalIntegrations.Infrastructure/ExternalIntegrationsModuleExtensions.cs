using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingMappings;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Pix;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;

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
    /// exactly. Gates which <see cref="IWhatsAppCredentialProvider"/>/
    /// <see cref="IWhatsAppWebhookCredentialProvider"/> is registered
    /// (Development in-memory vs. AWS Secrets Manager — Fase 12 CP5.3A) —
    /// the DbContext/schema and the real Meta connector itself are
    /// registered unconditionally in every environment (CP2.1 mandate §41:
    /// schema/config is not an external side-effect; CP5.3A:
    /// HomologRealWhatsAppRequired=true).
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

        // Fase 9, Checkpoint 3.2 — "Airbnb Deterministic Foundation".
        // Unconditional, same rationale as the WhatsApp repositories above:
        // plain table access, no secret, no external network call.
        services.AddScoped<IAirbnbIntegrationRepository, AirbnbIntegrationRepository>();
        services.AddScoped<IAirbnbListingMappingRepository, AirbnbListingMappingRepository>();
        services.AddScoped<IAirbnbReservationSyncPublisher, AirbnbReservationSyncPublisher>();

        // Fase 12, CP5.3A: outside Development, IWhatsAppCredentialProvider is
        // now backed by AWS Secrets Manager per-tenant secrets
        // (WhatsAppTenantSecretBackend=AWS_SECRETS_MANAGER_PER_TENANT)
        // instead of being left unregistered — HomologRealWhatsAppRequired=true.
        // Still never a silent fallback between the two implementations.
        if (isDevelopmentEnvironment)
            services.AddScoped<IWhatsAppCredentialProvider, DevelopmentWhatsAppCredentialProvider>();
        else
            services.AddScoped<IWhatsAppCredentialProvider, SecretsManagerWhatsAppCredentialProvider>();

        // Fase 9, Checkpoint 2.3.1 — webhook security ingress (ADR-022).
        // Deliberately separate, app/deployment-level abstraction (ADR-022
        // item 8/9): the webhook must verify its caller before any TenantId
        // is known, so it can never resolve credentials via the tenant-owned
        // WhatsAppIntegration/IWhatsAppCredentialProvider path. Same
        // Fase 12 CP5.3A treatment as IWhatsAppCredentialProvider above.
        if (isDevelopmentEnvironment)
            services.AddScoped<IWhatsAppWebhookCredentialProvider, DevelopmentWhatsAppWebhookCredentialProvider>();
        else
            services.AddScoped<IWhatsAppWebhookCredentialProvider, SecretsManagerWhatsAppWebhookCredentialProvider>();

        // Fase 12, CP5.3A: the two Secrets Manager-backed providers above
        // share one AmazonSecretsManagerClient (default AWS credential
        // chain - the ECS task role in Homolog/Production, never a key
        // configured here). Registered only outside Development, matching
        // this Infrastructure project's own precedent of never constructing
        // AWS SDK clients locally.
        if (!isDevelopmentEnvironment)
        {
            services.AddSingleton<Amazon.SecretsManager.IAmazonSecretsManager>(
                new Amazon.SecretsManager.AmazonSecretsManagerClient());
            services.AddSingleton<ISecretValueReader, AwsSecretsManagerValueReader>();
        }

        // Unconditional in every environment (unlike the credential provider
        // above): this is a stateless verification algorithm with no secret/
        // network dependency of its own — only the credential SOURCE is
        // Production-blocked, never the algorithm that consumes it.
        services.AddSingleton<IWebhookSignatureVerifier, MetaWebhookSignatureVerifier>();

        // Fase 12, Checkpoint 3 (Resilience & Rate Limiting) — the "Webhook"
        // HTTP category. Delegates to the shared IDistributedRateLimiter
        // (BuildingBlocks.Infrastructure, registered by IHostPro.Api's own
        // AddIHostProRateLimiting call) — this is the ONLY reason
        // WhatsAppWebhookController can use the rate limiter without
        // referencing Infrastructure directly (Api projects never do).
        services.AddSingleton<IWebhookRateLimiter, IHostPro.Contexts.ExternalIntegrations.Infrastructure.RateLimiting.WebhookRateLimiter>();

        // Fase 9, Checkpoint 2.3.2 — webhook status normalization
        // (ADR-022). Unconditional: no secret, no external network call —
        // just JSON parsing plus the route repository above.
        services.AddScoped<IWhatsAppWebhookStatusProcessor, MetaWebhookStatusProcessor>();

        // Fase 9, Checkpoint 2.3.3 — durable outbox + webhook status event
        // publishing (ADR-022 item 13). Unconditional, same rationale as the
        // processor above: no secret, no external network call, and the
        // webhook must durably publish in every environment, not just
        // Development (unlike the outbound send path's credential/connector
        // gates below).
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IExternalIntegrationsTransactionExecutor, ExternalIntegrationsOutboxTransactionExecutor>();
        services.AddScoped<IWhatsAppWebhookStatusEventPublisher, WhatsAppWebhookStatusEventPublisher>();

        // Fase 11, Checkpoint 1 — Inbound Conversation Foundation. Same
        // unconditional rationale as the status processor/publisher above:
        // no secret, no external network call, and inbound guest messages
        // must be normalized/published in every environment.
        services.AddScoped<IWhatsAppWebhookMessageProcessor, MetaWebhookMessageProcessor>();
        services.AddScoped<IWhatsAppWebhookMessageEventPublisher, WhatsAppWebhookMessageEventPublisher>();

        // Fase 9, Checkpoint 2.2 — real Meta Cloud API outbound connector.
        // Fase 12, CP5.3A: registration is now UNCONDITIONAL (previously
        // Development-only) — HomologRealWhatsAppRequired=true, and the
        // credential provider above already fails closed in every
        // non-Development environment if no real secret is configured, so
        // gating the connector itself is no longer needed for safety.
        //
        // Registered here so it is resolvable directly (e.g. by a dedicated
        // sandbox-proof test, or an explicit outbound send call) without
        // being wired into Communication's automatic ReservationCreated flow
        // — that flow still uses FakeWhatsAppConnector (mandate §46-49,
        // Option A: WhatsAppIntegration.IsEnabled stays false/unchanged).
        // Switching the automatic flow to the real connector is a distinct
        // business decision this checkpoint does not make.
        services.Configure<MetaWhatsAppOptions>(configuration.GetSection("ExternalIntegrations:WhatsApp:Meta"));

        var metaHttpClientBuilder = services.AddHttpClient(MetaWhatsAppMessagingProvider.HttpClientName, (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MetaWhatsAppOptions>>().Value;
            client.BaseAddress = new Uri("https://graph.facebook.com/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        // Fase 12, Checkpoint 3, Decision Gate amendment — same official
        // Microsoft.Extensions.Http.Resilience circuit breaker ONLY
        // shape as AIAgent.Infrastructure's own Anthropic wiring (see
        // its doc comment) — AddCircuitBreaker alone, never
        // AddRetry/AddHedging/AddTimeout. Meta already has
        // AutomaticMetaRetry=false (no retry of any kind existed before
        // this checkpoint, application-level or otherwise) — this stays
        // true; the circuit breaker only ever short-circuits a FUTURE
        // call, it never causes a second attempt of the current one.
        var metaCircuitBreakerOptions = configuration.GetSection("ExternalIntegrations:WhatsApp:Meta:CircuitBreaker").Get<MetaHttpCircuitBreakerOptions>()
            ?? new MetaHttpCircuitBreakerOptions();
        if (metaCircuitBreakerOptions.Enabled)
        {
            metaHttpClientBuilder.AddResilienceHandler("meta-circuit-breaker", builder =>
            {
                builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                {
                    FailureRatio = metaCircuitBreakerOptions.FailureRatio,
                    MinimumThroughput = metaCircuitBreakerOptions.MinimumThroughput,
                    SamplingDuration = metaCircuitBreakerOptions.SamplingDuration,
                    BreakDuration = metaCircuitBreakerOptions.BreakDuration,
                    // Same permanent-vs-transient split as MetaFailureCodes'
                    // own classification (never duplicated differently
                    // here): network errors/timeouts/429/5xx count;
                    // 400/401/403/404 (a permanently malformed/unauthorized
                    // request) never open the circuit.
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Exception is HttpRequestException or TaskCanceledException ||
                        (args.Outcome.Result is { } response &&
                            (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))),
                    OnOpened = _ =>
                    {
                        IHostPro.BuildingBlocks.Infrastructure.Resilience.CircuitBreakerTelemetry.RecordStateChange("Meta", "Opened");
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = _ =>
                    {
                        IHostPro.BuildingBlocks.Infrastructure.Resilience.CircuitBreakerTelemetry.RecordStateChange("Meta", "Closed");
                        return ValueTask.CompletedTask;
                    },
                    OnHalfOpened = _ =>
                    {
                        IHostPro.BuildingBlocks.Infrastructure.Resilience.CircuitBreakerTelemetry.RecordStateChange("Meta", "HalfOpened");
                        return ValueTask.CompletedTask;
                    },
                });
            });
        }

        services.AddScoped<IMessagingProvider, MetaWhatsAppMessagingProvider>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IPixProvider"/> (Fase 10, Checkpoint 5 — PIX/Payment
    /// Deterministic Foundation, ADR-025, synchronous exception #10) —
    /// deliberately a SEPARATE method from <see cref="AddExternalIntegrationsModule"/>,
    /// called only by the process that hosts Payments' own consumer
    /// (<c>IHostPro.Worker</c>), mirroring how <see cref="IMessagingProvider"/>'s
    /// real registration above is scoped to where it is actually consumed.
    /// Unconditional — <see cref="FakePixProvider"/> is this checkpoint's ONLY
    /// implementation, no real provider exists to gate against (unlike
    /// <see cref="IMessagingProvider"/>, which has both a fake, in
    /// Communication, and this real, Development-gated one).
    /// </summary>
    public static IServiceCollection AddExternalIntegrationsPixProvider(this IServiceCollection services)
    {
        services.AddScoped<IPixProvider, FakePixProvider>();
        return services;
    }
}
