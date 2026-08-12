using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Infrastructure.Caching;
using IHostPro.Contexts.Configuration.Infrastructure.Messaging;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;
using IHostPro.Contexts.Identity.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using JasperFx;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // Multi-tenant: for HTTP requests (IHostPro.Api) the tenant is resolved by an
    // authentication middleware; here, it is resolved once per consumed message by
    // TenantResolutionMiddleware below, from the TenantId carried by every
    // IntegrationEvent (Architecture Principles, Section 7).
    builder.Services.AddScoped<ITenantContext, TenantContext>();

    // See IHostPro.Api's Program.cs for the rationale (Incremento 2 plan, Etapa 9).
    builder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();

    // Tenant-aware transactional pipeline (TenantTransactionBehavior /
    // TenantBootstrapBehavior + ITenantAwareUnitOfWork) — foundation
    // registered now; no Command/Query dispatches through it yet, since no
    // handler exists until Incremento 2 (Incremento 1 plan, Section 12).
    builder.Services.AddIHostProTenantAwarePipeline();

    // Identity & Access module (Incremento 1 plan) — DbContext, custom
    // Identity stores/hasher/validator, tenant bootstrap reader.
    // isDevelopmentEnvironment gates the Development-only tenant/user seed
    // configuration (Incremento 2 plan, ajuste 3-4) — see IHostPro.Api's
    // Program.cs for the corresponding registration.
    builder.Services.AddIdentityModule(builder.Configuration, builder.Environment.IsDevelopment());

    // Configuration & Policy's cache (Fase 5, Incremento 1, Checkpoint 6) —
    // only the cache is needed here, never ConfigurationDbContext/the typed
    // readers/AddConfigurationModule: Worker's only job for this context is
    // invalidating PolicyUpdated's cache entries, never reading or writing
    // ConfigurationDbContext directly. Must point at the same physical Redis
    // IHostPro.Api's own AddConfigurationModule (-> AddConfigurationPolicyCache)
    // uses, or an invalidation here would never be visible to Api's reads.
    builder.Services.AddConfigurationPolicyCache(builder.Configuration);
    builder.Services.AddScoped<IIntegrationEventHandler<PolicyUpdated>, PolicyUpdatedCacheInvalidation>();

    // Housekeeping module (Fase 6, Incremento 1) — unlike Configuration &
    // Policy, Worker DOES need the full module here: its own local
    // Property/Reservation projections and the automatic
    // ReservationCancelled -> Cleaning-cancellation reaction are both real
    // writes to HousekeepingDbContext, consumed exclusively in this process
    // — see HousekeepingModuleExtensions' own doc comment.
    builder.Services.AddHousekeepingModule(builder.Configuration);

    // IHostPro.Worker hosts every Bounded Context's message handlers and Sagas,
    // kept in a separate process from IHostPro.Api so message processing can
    // scale independently of HTTP traffic (Architecture Principles, Section 2).
    // Handlers are plain classes discovered by Wolverine's naming convention —
    // no Bounded Context ever implements a Wolverine-specific interface
    // (Architecture Principles, Section 11; ADR-004).
    // Wolverine's own Main message store (mirrors IHostPro.Api's Program.cs
    // — see its own comment for the full InvalidWolverineStorageConfigurationException
    // rationale: exactly one Main store is required whenever any Ancillary
    // store exists). platform_messaging carries no domain event and is
    // provisioned exclusively by IHostPro.MigrationRunner, never by this
    // process.
    var platformMessagingConnectionString = builder.Configuration.GetConnectionString("Platform")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Platform'.");

    builder.UseWolverine(opts =>
    {
        opts.UseIHostProRabbitMq(builder.Configuration, listen: true);

        // Checkpoint 6 homologação, real defect found and fixed: Housekeeping's
        // Worker-side reactions (ReservationProjectionAndCancellationReaction,
        // PropertyProjectionSynchronizer) depend transitively, via
        // IHousekeepingTransactionExecutor -> HousekeepingOutboxTransactionExecutor,
        // on IDbContextOutbox<HousekeepingDbContext> — a service Wolverine
        // only registers once its own Postgres-backed storage is enrolled.
        // Without the three calls below, a REAL message delivered by RabbitMQ
        // crashed Wolverine's own DI-resolution codegen with
        // "Cannot build service type IIntegrationEventHandler<...> in any
        // way" (every prior test masked this by calling
        // IIntegrationEventHandler<T>.HandleAsync directly, bypassing
        // Wolverine's real handler-resolution pipeline entirely).
        // PolicyUpdatedCacheInvalidation needed none of this precisely
        // because it depends only on IPolicyCacheInvalidator (a plain Redis
        // client), never on the outbox — confirmed by direct comparison.
        opts.PersistMessagesWithPostgresql(platformMessagingConnectionString, "platform_messaging");

        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Housekeeping")!,
            "housekeeping_messaging",
            typeof(HousekeepingDbContext));

        // Required in addition to EnrollAncillaryPostgresqlOutbox above —
        // same empirically-confirmed requirement documented in
        // IHostPro.Api's Program.cs for Identity's own outbox: without this,
        // IDbContextOutbox<HousekeepingDbContext> never gets registered by
        // Wolverine's DI wiring at all. CONFIRMED by direct experiment
        // (Checkpoint 6 tenant-identity investigation, Configuração B):
        // removing this call reproduces the exact original crash
        // ("Cannot build service type IIntegrationEventHandler<...>") — this
        // is not optional, and is unrelated to the separate tenant-identity
        // defect investigated alongside it (see
        // ReservationProjectionAndCancellationReaction's own doc comment).
        opts.UseEntityFrameworkCoreTransactions();

        // No host may create/alter the outbox's schema/tables at runtime —
        // only IHostPro.MigrationRunner may (mirrors IHostPro.Api's Program.cs).
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        // ADR-015 (Fase 6, Checkpoint 6) — IHousekeepingMessageExecutionScope
        // is the single, deliberately-authorized place in Housekeeping that
        // holds IServiceScopeFactory (registered Singleton), so Wolverine's
        // codegen cannot statically inline its construction and needs to
        // fall back to a runtime service-location call for THIS type only —
        // confirmed via Wolverine.Configuration.InvalidServiceLocationException
        // ("Found service locations... ServiceLocationPolicy.NotAllowed is
        // in effect") the first time a real message reached this chain. This
        // targeted opt-out (public API, JasperFx.CodeGeneration.GenerationRules)
        // is scoped to exactly this one type — it does not weaken Wolverine's
        // strict codegen for any other chain (PolicyUpdated included).
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Housekeeping.Application.IHousekeepingMessageExecutionScope>();

        opts.Policies.AddMiddleware(
            typeof(TenantResolutionMiddleware),
            chain => typeof(IntegrationEvent).IsAssignableFrom(chain.MessageType));

        // Configuration & Policy's first (and, this increment, only) consumed
        // Integration Event (Checkpoint 6) — PolicyUpdatedHandler lives in
        // Configuration.Infrastructure, a separate assembly from this entry
        // assembly, so it must be explicitly included in Wolverine's handler
        // discovery (which otherwise only scans the assembly UseWolverine is
        // called from).
        //
        // Checkpoint 7 homologação, real defect found and fixed: this used
        // to be opts.Publish(x => { x.Message<PolicyUpdated>(); x.ToRabbitTopics(...); }),
        // which is a SENDER-side routing rule — confirmed against Wolverine's
        // own RabbitMQ documentation, and by direct observation against a
        // real running broker (the queue never even got created), that a
        // Publish rule never makes a process listen to anything. The queue
        // itself, and its binding to the configuration-events topic exchange
        // with routing key policy_updated, is now provisioned exclusively by
        // IHostPro.MigrationRunner (same single-provisioning-authority
        // pattern already used for every other messaging object in this
        // platform) — this process only attaches a consumer to the
        // already-existing queue.
        opts.Discovery.IncludeAssembly(typeof(PolicyUpdatedHandler).Assembly);
        opts.ListenToRabbitQueue("configuration.policy-updated");

        // Housekeeping's consumed Integration Events (Fase 6, Incremento 1,
        // Checkpoint 3) — two queues, each bound to MULTIPLE routing keys of
        // an EXISTING exchange owned by another Bounded Context
        // (property-management-events, reservation-events), never a new
        // exchange of its own for these — standard decoupled pub/sub: the
        // publishing context (Property Management/Reservations) never knows
        // Housekeeping is listening. Both queues, and their bindings, are
        // provisioned exclusively by IHostPro.MigrationRunner (same
        // single-provisioning-authority pattern as every other messaging
        // object in this platform) — this process only attaches a consumer
        // to each already-existing queue. PropertyProjectionSynchronizer/
        // ReservationProjectionAndCancellationReaction live in
        // Housekeeping.Infrastructure, a separate assembly from this entry
        // assembly, so it must be explicitly included in Wolverine's handler
        // discovery.
        opts.Discovery.IncludeAssembly(typeof(PropertyCreatedHandler).Assembly);
        opts.ListenToRabbitQueue("housekeeping.property-projection");
        opts.ListenToRabbitQueue("housekeeping.reservation-projection");

        // Real defect found and fixed (Checkpoint 6 homologação, ADR-015
        // spike): IHostPro.Api/Program.cs already routes every Housekeeping
        // Cleaning lifecycle event (CleaningCreated/Assigned/Started/
        // InspectionStarted/Completed/Cancelled) to the housekeeping-events
        // topic exchange, since Api is where every HTTP-triggered lifecycle
        // transition runs. CleaningCancelled is the ONE Housekeeping event
        // also published from THIS process — ReservationProjectionAndCancellationReaction's
        // automatic reaction to a real ReservationCancelled — and this
        // process had no matching Publish rule at all, confirmed by a real
        // end-to-end test: the event was staged/flushed through the outbox
        // without error, but never actually routed onto the broker. Mirrors
        // IHostPro.Api's own RouteHousekeepingEvent<CleaningCancelled> rule
        // exactly (same exchange, same routing key) — the exchange itself
        // is provisioned exclusively by IHostPro.MigrationRunner, never by
        // either host process.
        const string housekeepingEventsExchange = "housekeeping-events";
        opts.PublishMessage(typeof(CleaningCancelled))
            .ToRabbitRoutingKey(housekeepingEventsExchange, "cleaning_cancelled", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);
    });

    // Application layers depend only on IEventPublisher (BuildingBlocks.Messaging.Abstractions),
    // never on Wolverine types directly (Architecture Principles, Section 11).
    builder.Services.AddScoped<IEventPublisher, WolverineEventPublisher>();

    // OTLP endpoint is configured exclusively via appsettings/environment variables
    // (never hardcoded) — pipeline: App -> OTLP -> OpenTelemetry Collector -> Prometheus
    // -> Grafana (ADR-007). "OpenTelemetry__OtlpEndpoint" overrides it per environment.
    var otlpEndpoint = new Uri(builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName: "IHostPro.Worker"))
        .WithTracing(tracing => tracing.AddOtlpExporter(o => o.Endpoint = otlpEndpoint))
        .WithMetrics(metrics => metrics
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = otlpEndpoint));

    var host = builder.Build();
    host.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "IHostPro.Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
