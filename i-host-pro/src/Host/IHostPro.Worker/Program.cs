using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Infrastructure.Caching;
using IHostPro.Contexts.Configuration.Infrastructure.Messaging;
using IHostPro.Contexts.Dashboard.Infrastructure;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Messaging;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.Contexts.Workflow.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;

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

    // Reservations module — Agenda Foundation slice only (Fase 7, Incremento
    // 1, Checkpoint 1): Worker needs ReservationsDbContext + the minimal
    // projection-consumer registrations to keep CleaningScheduleProjection in
    // sync with Housekeeping's own Cleaning events — never the full
    // AddReservationsCommandDispatch (HTTP command/query dispatch, Api-only
    // by that method's own design) — see ReservationsModuleExtensions'
    // AddReservationsScheduleProjectionConsumer doc comment.
    builder.Services.AddReservationsModule(builder.Configuration);
    builder.Services.AddReservationsScheduleProjectionConsumer();

    // Dashboard & Reporting module (Fase 7, Incremento 2): DashboardDbContext
    // + the four projection synchronizers' full DI graph, so the tenant-safe
    // execution boundary (IDashboardMessageExecutionScope, ADR-016) can
    // construct each, from its own child DI scope, for every consumed event
    // — mirrors AddReservationsModule + AddReservationsScheduleProjectionConsumer's
    // own two-call split exactly (Checkpoint 2 added AddDashboardModule to
    // IHostPro.Api too, for the new Overview query — see
    // DashboardModuleExtensions' own doc comment).
    builder.Services.AddDashboardModule(builder.Configuration);
    builder.Services.AddDashboardProjectionConsumer();

    // Workflow Orchestration module (Fase 8, Checkpoint 1 — ADR-018):
    // stateless — no DbContext, no aggregates, no persistence (approved
    // Decision Material 4) — so, unlike every module above, there is no
    // configuration to pass and no separate "module + consumer" split.
    // Consumed exclusively in this process; IHostPro.Api never references
    // it (no HTTP surface this checkpoint).
    builder.Services.AddWorkflowModule();

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

        // Reservations' own durable outbox (Fase 3, Incremento 1 — publish
        // side, IHostPro.Api). Enrolled here too from Fase 7, Incremento 1
        // (Agenda Foundation, Checkpoint 1) — this is the FIRST checkpoint
        // Reservations consumes any message; the ancillary store must be
        // enrolled in THIS process for IDbContextOutbox<ReservationsDbContext>/
        // IReservationsTransactionExecutor to resolve inside a Wolverine
        // handler (same empirically-confirmed requirement as Housekeeping's
        // own — see the comment on UseEntityFrameworkCoreTransactions below).
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Reservations")!,
            "reservations_messaging",
            typeof(ReservationsDbContext));

        // Dashboard & Reporting's own durable outbox (Fase 7, Incremento 2,
        // Checkpoint 1) — this is the FIRST checkpoint Dashboard consumes
        // any message; the ancillary store must be enrolled in THIS process
        // for IDbContextOutbox<DashboardDbContext>/IDashboardTransactionExecutor
        // to resolve inside a Wolverine handler (same empirically-confirmed
        // requirement as Housekeeping's/Reservations' own).
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Dashboard")!,
            "dashboard_messaging",
            typeof(DashboardDbContext));

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

        // ADR-016 (Fase 7, Checkpoint 1 CLOSURE) — same rationale as
        // Housekeeping's own AlwaysUseServiceLocationFor above:
        // ReservationsMessageExecutionScope is the single, deliberately-
        // authorized place in Reservations that holds IServiceScopeFactory,
        // so Wolverine's codegen needs this explicit opt-out for exactly
        // this one type.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Reservations.Application.IReservationsMessageExecutionScope>();

        // ADR-016, third application (Fase 7, Incremento 2, Checkpoint 1) —
        // same rationale as Housekeeping's/Reservations' own
        // AlwaysUseServiceLocationFor above: DashboardMessageExecutionScope
        // is the single, deliberately-authorized place in Dashboard that
        // holds IServiceScopeFactory.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Dashboard.Application.IDashboardMessageExecutionScope>();

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

        // Fase 8, Checkpoint 1 (Workflow Orchestration — ADR-018): Housekeeping's
        // consumed cross-context COMMAND, CreateCleaningForReservation —
        // same assembly as PropertyCreatedHandler above, already included.
        // The queue itself, and its binding to the dedicated
        // workflow-orchestration-commands exchange, is provisioned
        // exclusively by IHostPro.MigrationRunner.
        opts.ListenToRabbitQueue("housekeeping.workflow-commands");

        // Reservations' first consumed Integration Events (Fase 7, Incremento
        // 1 — Agenda Foundation, Checkpoint 1): the ten Cleaning lifecycle
        // events IHostPro.Api actually routes to housekeeping-events —
        // every real Cleaning event except CleaningDelayed (CleaningCreated/
        // Assigned/InTransit/Started/InspectionStarted/Completed/
        // Interrupted/NeedsHelp/NeedsMaterial/Cancelled) — generalized from
        // an initial CleaningCreated-only real
        // transport proof, CleaningCreatedScheduleProjectionWorkerRoundTripTests).
        // The handlers live in Reservations.Infrastructure, a separate
        // assembly from this entry assembly, so it must be explicitly
        // included in Wolverine's handler discovery. The queue itself, and
        // its bindings to the housekeeping-events topic exchange, are
        // provisioned exclusively by IHostPro.MigrationRunner (same
        // single-provisioning-authority pattern as every other messaging
        // object in this platform) — this process only attaches a consumer
        // to the already-existing queue.
        opts.Discovery.IncludeAssembly(typeof(CleaningCreatedHandler).Assembly);
        opts.ListenToRabbitQueue("reservations.cleaning-schedule-projection");

        // Dashboard & Reporting's consumed Integration Events (Fase 7,
        // Incremento 2, Checkpoint 1) — four queues, each bound to MULTIPLE
        // routing keys of an EXISTING exchange owned by another Bounded
        // Context (property-management-events, reservation-events,
        // housekeeping-events), never a new exchange of its own — same
        // decoupled pub/sub pattern as Housekeeping's/Reservations' own
        // queues above: the publishing context never needs to know
        // Dashboard is listening. All four queues, and their bindings, are
        // provisioned exclusively by IHostPro.MigrationRunner. The four
        // projection synchronizers live in Dashboard.Infrastructure, a
        // separate assembly from this entry assembly, so it must be
        // explicitly included in Wolverine's handler discovery. Fully
        // qualified below (never a blanket `using`) because
        // Housekeeping.Infrastructure.Messaging/Reservations.Infrastructure.Messaging
        // already declare their own same-named handler classes
        // (ReservationCreatedHandler, PropertyCreatedHandler, etc.) for
        // different events — a blanket using would collide.
        opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.ReservationCreatedHandler).Assembly);
        opts.ListenToRabbitQueue("dashboard.reservation-projection");
        opts.ListenToRabbitQueue("dashboard.cleaning-projection");
        opts.ListenToRabbitQueue("dashboard.property-projection");
        opts.ListenToRabbitQueue("dashboard.occurrence-projection");

        // Workflow Orchestration's own single trigger consumer (Fase 8,
        // Checkpoint 1 — ADR-018): a fourth, independent subscriber queue
        // on the EXISTING reservation-events exchange (Reservations never
        // needs to know Workflow is listening — same decoupled pub/sub
        // pattern as Housekeeping's/Dashboard's own queues above). The
        // queue itself, and its binding, is provisioned exclusively by
        // IHostPro.MigrationRunner. ReservationCreatedHandler lives in
        // Workflow.Infrastructure, a separate assembly from this entry
        // assembly, so it must be explicitly included in Wolverine's
        // handler discovery — fully qualified below (never a blanket
        // `using`) for the same collision reason as Dashboard's own
        // ReservationCreatedHandler above.
        opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly);
        opts.ListenToRabbitQueue("workflow.reservation-created-trigger");

        // Real defect found and fixed (Checkpoint 6 homologação, ADR-015
        // spike): IHostPro.Api/Program.cs already routes every real Cleaning
        // lifecycle event except CleaningDelayed (Fase 7, Incremento 1,
        // Checkpoint 1 closure — generalized from the smaller Fase 6 list)
        // to the housekeeping-events topic exchange, since Api is where
        // every HTTP-triggered lifecycle
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

        // Fase 8, Checkpoint 1 (Workflow Orchestration — ADR-018): the
        // codebase's first cross-context COMMAND. Sent via IMessageBus.SendAsync
        // (see CreateCleaningOnReservationCreated) rather than PublishAsync —
        // there is exactly one destination Bounded Context (Housekeeping) —
        // but the routing CONFIGURATION method is still PublishMessage,
        // Wolverine's own fixed API name for "configure how this message
        // type is routed" regardless of Send/Publish semantics at the call
        // site (same as every other rule in this block). A dedicated,
        // narrowly-scoped Direct exchange — never the generic/topic
        // *-events exchanges above — makes clear this is not a fan-out
        // event. No ancillary outbox enrollment needed: Workflow owns no
        // DbContext, so this durable send uses Wolverine's own Main store
        // (platform_messaging, already configured above) by default.
        const string workflowOrchestrationCommandsExchange = "workflow-orchestration-commands";
        opts.PublishMessage(typeof(CreateCleaningForReservation))
            .ToRabbitRoutingKey(
                workflowOrchestrationCommandsExchange, "create_cleaning_for_reservation",
                exchange => exchange.ExchangeType = ExchangeType.Direct)
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
