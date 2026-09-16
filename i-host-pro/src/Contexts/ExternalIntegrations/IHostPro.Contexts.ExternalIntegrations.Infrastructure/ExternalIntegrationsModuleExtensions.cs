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

        // Airbnb Email Bridge — persistence gate + real Microsoft OAuth gate.
        // Unconditional in every environment, same rationale as the Airbnb
        // repositories above: PostgreSQL + local configuration only, no AWS
        // dependency. The encryption key and ClientId are both resolved
        // lazily on first use (see AesGcmTokenCacheProtector/
        // MsalAirbnbEmailAuthenticator), so a missing value never blocks
        // host startup for tenants not using this feature. Graph polling and
        // the Airbnb email parser remain later, separately authorized gates.
        services.AddSingleton<IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.ITokenCacheProtector,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.AesGcmTokenCacheProtector>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailUnitOfWork,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.AirbnbEmailUnitOfWork>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailTokenCacheStore,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.PostgresAirbnbEmailTokenCacheStore>();
        services.Configure<IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.AirbnbEmailBridgeOptions>(
            configuration.GetSection("ExternalIntegrations:AirbnbEmailBridge"));
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailMailboxConnectionRepository,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.AirbnbEmailMailboxConnectionRepository>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailAuthenticator,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.MsalAirbnbEmailAuthenticator>();

        // Airbnb Reservation Email Parser gate — DRY_RUN only. Automatic
        // per-receipt Worker publication remains NOT wired (a distinct,
        // not-yet-made "Automatic Publication Design Gate" decision) — this
        // registration only makes the pieces resolvable, e.g. for a future
        // admin mapping endpoint, a controlled one-shot smoke, or a
        // deliberately separate orchestration step. AirbnbListingTitleMapping
        // is a separate, exact-title-only resolution mechanism from
        // AirbnbListingMapping above (no stable Airbnb listing id is ever
        // observed in these emails).
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings.IAirbnbListingTitleMappingRepository,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.AirbnbListingTitleMappingRepository>();
        services.AddSingleton<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser.IAirbnbReservationReminderParser,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbReservationParser.AirbnbReservationReminderParser>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser.IAirbnbReservationDryRunEvaluator,
            IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser.AirbnbReservationDryRunEvaluator>();

        // Resolved-Property Publication Bridge gate — publishes the SAME
        // AirbnbReservationImported event as IAirbnbReservationSyncPublisher
        // above, but for callers (like the Email Bridge's
        // AirbnbListingTitleMapping resolution) that already resolved a
        // PropertyId themselves and have no stable Airbnb listing id to
        // resolve via AirbnbListingMapping. Deliberately a separate
        // interface, not an overload — see IAirbnbResolvedReservationSyncPublisher's
        // own doc comment. Still never called by the Worker's normal delta
        // polling (same Automatic Publication Design Gate boundary above).
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports.IAirbnbResolvedReservationSyncPublisher,
            AirbnbResolvedReservationSyncPublisher>();

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

    /// <summary>
    /// Real Tenant WhatsApp Activation Readiness gate (SMALL_IMPLEMENTATION_GAP
    /// plan): registers exactly what <see cref="MetaWhatsAppMessagingProvider"/>
    /// needs to run in <c>IHostPro.Worker</c> — the ONLY process that resolves
    /// <c>Communication</c>'s <c>IOutboundMessageConnector</c> for a real send
    /// (<c>SendAgentResponseCommand</c>'s handler, reached from the AI Agent's
    /// Wolverine-hosted consumer). Deliberately a SEPARATE method from
    /// <see cref="AddExternalIntegrationsModule"/> rather than that method being
    /// called wholesale from Worker too: <see cref="AddExternalIntegrationsModule"/>
    /// also registers the webhook-only surface (<c>IWebhookRateLimiter</c> needs
    /// <c>IDistributedRateLimiter</c>, registered only by <c>IHostPro.Api</c>'s own
    /// <c>AddIHostProRateLimiting</c>; the Airbnb/tenant-route repositories are
    /// webhook-only concerns) — Worker never hosts any controller, so none of
    /// that is reachable there, and <c>Host.CreateApplicationBuilder</c>'s
    /// default <c>ValidateOnBuild=true</c> would fail Worker's startup on the
    /// unresolvable <c>IDistributedRateLimiter</c> dependency if the whole
    /// module were registered instead. This intentionally duplicates a few
    /// registrations already in <see cref="AddExternalIntegrationsModule"/>
    /// (same accepted trade-off as <c>AwsSecretsManagerValueReader</c> existing
    /// separately in AIAgent.Infrastructure too) rather than refactoring the
    /// existing, already-Api-proven method.
    /// </summary>
    public static IServiceCollection AddExternalIntegrationsWhatsAppOutboundProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopmentEnvironment)
    {
        services.AddDbContext<ExternalIntegrationsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("ExternalIntegrations"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations")));

        services.AddScoped<IWhatsAppIntegrationRepository, WhatsAppIntegrationRepository>();
        services.AddScoped<IWhatsAppTemplateMappingRepository, WhatsAppTemplateMappingRepository>();

        if (isDevelopmentEnvironment)
            services.AddScoped<IWhatsAppCredentialProvider, DevelopmentWhatsAppCredentialProvider>();
        else
            services.AddScoped<IWhatsAppCredentialProvider, SecretsManagerWhatsAppCredentialProvider>();

        if (!isDevelopmentEnvironment)
        {
            services.AddSingleton<Amazon.SecretsManager.IAmazonSecretsManager>(
                new Amazon.SecretsManager.AmazonSecretsManagerClient());
            services.AddSingleton<ISecretValueReader, AwsSecretsManagerValueReader>();
        }

        services.Configure<MetaWhatsAppOptions>(configuration.GetSection("ExternalIntegrations:WhatsApp:Meta"));

        var metaHttpClientBuilder = services.AddHttpClient(MetaWhatsAppMessagingProvider.HttpClientName, (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MetaWhatsAppOptions>>().Value;
            client.BaseAddress = new Uri("https://graph.facebook.com/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

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
    /// Registers exactly what <c>AirbnbEmailDeltaPollingBackgroundService</c>
    /// needs in <c>IHostPro.Worker</c> — mirrors
    /// <see cref="AddExternalIntegrationsWhatsAppOutboundProvider"/>'s own
    /// rationale for being a separate, trimmed method rather than the full
    /// <see cref="AddExternalIntegrationsModule"/> (Worker never hosts a
    /// controller, so the Api-only webhook surface must stay unreachable
    /// there). Re-registers <see cref="ExternalIntegrationsDbContext"/> with
    /// the identical configuration <see cref="AddExternalIntegrationsWhatsAppOutboundProvider"/>
    /// already does — <c>AddDbContext</c>'s own <c>TryAdd</c> semantics make
    /// this safe/idempotent, the same accepted redundancy already present
    /// between this class's own two existing registration methods.
    ///
    /// Also registers <see cref="IIntegrationEventCollector"/>/
    /// <see cref="IExternalIntegrationsTransactionExecutor"/>/
    /// <see cref="IAirbnbResolvedReservationSyncPublisher"/> (Automatic
    /// Publication Design + Safety gate) — the delta sync runner's own
    /// activated-flow branch invokes the resolved-property publisher
    /// directly, which drains/publishes this context's Wolverine outbox.
    /// This is now safe to resolve here ONLY because <c>IHostPro.Worker</c>'s
    /// own <c>Program.cs</c> was updated in the same gate to enroll the
    /// <c>external_integrations_messaging</c> ancillary store — the earlier
    /// version of this doc comment is no longer accurate and existed
    /// specifically to explain why that enrollment was missing. Still uses
    /// <see cref="IAirbnbEmailUnitOfWork"/> for the runner's own
    /// receipt/sync-state writes (a distinct, narrower unit of work than the
    /// outbox-draining executor).
    /// </summary>
    public static IServiceCollection AddExternalIntegrationsAirbnbEmailBridgeWorker(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ExternalIntegrationsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("ExternalIntegrations"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations")));

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.ITokenCacheProtector,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.AesGcmTokenCacheProtector>();
        services.Configure<IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.AirbnbEmailBridgeOptions>(
            configuration.GetSection("ExternalIntegrations:AirbnbEmailBridge"));

        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailTokenCacheStore,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.PostgresAirbnbEmailTokenCacheStore>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailMailboxConnectionRepository,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.AirbnbEmailMailboxConnectionRepository>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailSyncStateRepository,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.AirbnbEmailSyncStateRepository>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailMessageReceiptRepository,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.AirbnbEmailMessageReceiptRepository>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailAuthenticator,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.MsalAirbnbEmailAuthenticator>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailUnitOfWork,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.AirbnbEmailUnitOfWork>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailDeltaSyncRunner,
            IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.AirbnbEmailDeltaSyncRunner>();

        services.AddHttpClient(
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.MicrosoftGraphEmailMessageSource.HttpClientName);
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge.IAirbnbEmailMessageSource,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge.MicrosoftGraphEmailMessageSource>();

        // Airbnb Reservation Email Parser gate (DRY_RUN only) - the delta sync
        // runner resolves this to turn a supported real message into DRY_RUN
        // evidence on its own receipt row; never wired to IAirbnbReservationSyncPublisher.
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings.IAirbnbListingTitleMappingRepository,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.AirbnbListingTitleMappingRepository>();
        services.AddSingleton<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser.IAirbnbReservationReminderParser,
            IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbReservationParser.AirbnbReservationReminderParser>();
        services.AddScoped<IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser.IAirbnbReservationDryRunEvaluator,
            IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser.AirbnbReservationDryRunEvaluator>();

        // Automatic Publication Design + Safety gate - required for the
        // delta sync runner's activated-flow branch to actually invoke the
        // resolved-property publisher and durably enqueue its event. Safe
        // ONLY because IHostPro.Worker's own Program.cs now enrolls the
        // external_integrations_messaging ancillary outbox store (see this
        // method's own doc comment).
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IExternalIntegrationsTransactionExecutor, ExternalIntegrationsOutboxTransactionExecutor>();
        services.AddScoped<IAirbnbResolvedReservationSyncPublisher, AirbnbResolvedReservationSyncPublisher>();

        return services;
    }
}
