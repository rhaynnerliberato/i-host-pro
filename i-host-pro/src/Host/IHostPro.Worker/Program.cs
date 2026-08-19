using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Infrastructure;
using IHostPro.Contexts.Configuration.Infrastructure.Caching;
using IHostPro.Contexts.Configuration.Infrastructure.Messaging;
using IHostPro.Contexts.Communication.Infrastructure;
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

    // Configuration & Policy (Fase 5, Incremento 1, Checkpoint 6 — cache;
    // Fase 9, Checkpoint 1 — ConfigurationDbContext/ITemplateReader). Through
    // Fase 8 this process only needed the cache (invalidating PolicyUpdated's
    // entries), never ConfigurationDbContext/the typed readers directly.
    // Communication's own Wolverine consumer (Fase 9) is the first thing in
    // this process that needs ITemplateReader — resolving the active
    // Template for a ReservationCreated trigger — so this now calls the full
    // AddConfigurationModule (which itself still calls
    // AddConfigurationPolicyCache internally, pointed at the same physical
    // Redis IHostPro.Api uses, unchanged) rather than only the cache.
    builder.Services.AddConfigurationModule(builder.Configuration);
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

    // Communication module (Fase 9, Checkpoint 1): CommunicationDbContext +
    // the ReservationCreated-triggered messaging consumer's full DI graph,
    // so the tenant-safe execution boundary (ICommunicationMessageExecutionScope,
    // ADR-016) can construct it from its own child DI scope — mirrors
    // AddDashboardModule + AddDashboardProjectionConsumer's own two-call
    // split exactly. Communication publishes no Integration Event this
    // checkpoint and has no HTTP surface, so IHostPro.Api never references
    // this module (CommunicationModuleExtensions' own doc comment).
    //
    // Gated to Development ONLY (CP1 closure — corrective homologation):
    // AddCommunicationReservationConsumer registers the ONLY
    // IOutboundMessageConnector this checkpoint has — FakeWhatsAppConnector,
    // which always reports success without ever calling a real WhatsApp
    // provider (none is contracted/implemented until Checkpoint 2). Without
    // this gate, a real ReservationCreated in any non-Development
    // environment would silently mark a Message as Sent despite nothing
    // ever being delivered — a false operational positive.
    //
    // Deliberately an ALLOWLIST (IsDevelopment()), never a denylist
    // (!IsProduction()): the first corrective pass used !IsProduction(),
    // which would have also left the fake connector active in Staging/QA/
    // UAT/any custom environment name — the same false-positive risk this
    // gate exists to close, just relocated. IsDevelopment() is the only
    // condition under which this fake automation may run.
    //
    // Every existing test (WorkerRoundTrip suite, WebE2EFixture) explicitly
    // launches this process with DOTNET_ENVIRONMENT=Development (the
    // variable this Generic Host process actually reads — see the
    // ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT note below), so this gate
    // does not disable Communication in any of them.
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddCommunicationModule(builder.Configuration);
        builder.Services.AddCommunicationReservationConsumer();
    }

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

        // ADR-016, fourth application (Fase 9, Checkpoint 1) — same rationale
        // as Housekeeping's/Reservations'/Dashboard's own
        // AlwaysUseServiceLocationFor above: CommunicationMessageExecutionScope
        // is the single, deliberately-authorized place in Communication that
        // holds IServiceScopeFactory.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Communication.Application.ICommunicationMessageExecutionScope>();

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
        // ADR-020 (cross-phase corrective fix — Wolverine handler-chain
        // isolation for cross-context event fan-out): PolicyUpdated has
        // exactly one Wolverine-discovered handler class in this process
        // (PolicyUpdatedHandler, which itself resolves the keyed
        // IIntegrationEventHandler<PolicyUpdated> registrations via ordinary
        // DI) — this is the ALREADY-homologated keyed-DI pattern, a
        // different problem to the one ADR-020 fixes, and needs no sticky
        // handler mapping: a message type with only one discovered handler
        // class was never at risk of Wolverine's default handler-combining
        // behaviour in the first place.
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

        // ADR-020: PropertyCreated/Activated/Deactivated/Archived and
        // ReservationCreated/Cancelled are each independently consumed by
        // Housekeeping AND at least one other bounded context (see the
        // fan-out inventory in the ADR) — without AddStickyHandler, Wolverine
        // combines every discovered handler for the same CLR message type
        // into one shared chain regardless of listening endpoint, so a
        // delivery to ANY of the affected queues could run another bounded
        // context's handler logic instead of (or in addition to) this one.
        // Sticky-binding this queue's own four handler TYPES keeps it
        // strictly isolated to Housekeeping's own logic; no topology change
        // (same queue, same bindings, still provisioned by MigrationRunner).
        opts.ListenToRabbitQueue("housekeeping.property-projection")
            .AddStickyHandler(typeof(PropertyCreatedHandler))
            .AddStickyHandler(typeof(PropertyActivatedHandler))
            .AddStickyHandler(typeof(PropertyDeactivatedHandler))
            .AddStickyHandler(typeof(PropertyArchivedHandler));

        opts.ListenToRabbitQueue("housekeeping.reservation-projection")
            .AddStickyHandler(typeof(ReservationCreatedHandler))
            .AddStickyHandler(typeof(ReservationCancelledHandler));

        // Fase 8, Checkpoint 1 (Workflow Orchestration — ADR-018): Housekeeping's
        // consumed cross-context COMMAND, CreateCleaningForReservation —
        // same assembly as PropertyCreatedHandler above, already included.
        // The queue itself, and its binding to the dedicated
        // workflow-orchestration-commands exchange, is provisioned
        // exclusively by IHostPro.MigrationRunner. Single consumer by design
        // (ADR-018) — not at risk, no sticky mapping needed.
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

        // ADR-020: all ten Cleaning lifecycle events are independently
        // consumed by Reservations/Agenda AND Dashboard — sticky-bind this
        // queue's own ten handler types so a delivery here can never run
        // Dashboard's handler logic instead (same rationale as Housekeeping's
        // own sticky mapping above).
        opts.ListenToRabbitQueue("reservations.cleaning-schedule-projection")
            .AddStickyHandler(typeof(CleaningCreatedHandler))
            .AddStickyHandler(typeof(CleaningAssignedHandler))
            .AddStickyHandler(typeof(CleaningInTransitHandler))
            .AddStickyHandler(typeof(CleaningStartedHandler))
            .AddStickyHandler(typeof(CleaningInspectionStartedHandler))
            .AddStickyHandler(typeof(CleaningCompletedHandler))
            .AddStickyHandler(typeof(CleaningInterruptedHandler))
            .AddStickyHandler(typeof(CleaningNeedsHelpHandler))
            .AddStickyHandler(typeof(CleaningNeedsMaterialHandler))
            .AddStickyHandler(typeof(CleaningCancelledHandler));

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

        // ADR-020: ReservationCreated and ReservationCancelled are each
        // shared with at least one other bounded context (see the fan-out
        // inventory) — ReservationUpdated is Dashboard's own, exclusive
        // event (no other consumer exists anywhere in this process), so it
        // is left un-sticky: a message type with only one discovered
        // handler was never at risk of Wolverine's combining behaviour.
        opts.ListenToRabbitQueue("dashboard.reservation-projection")
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.ReservationCreatedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.ReservationCancelledHandler));

        // ADR-020: all ten Cleaning lifecycle events are shared with
        // Reservations/Agenda (see reservations.cleaning-schedule-projection
        // above) — sticky-bind Dashboard's own ten handler types here too.
        opts.ListenToRabbitQueue("dashboard.cleaning-projection")
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningCreatedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningAssignedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningInTransitHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningStartedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningInspectionStartedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningCompletedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningInterruptedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningNeedsHelpHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningNeedsMaterialHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningCancelledHandler));

        // ADR-020: all four Property events are shared with Housekeeping
        // (see housekeeping.property-projection above) — sticky-bind
        // Dashboard's own four handler types here too.
        opts.ListenToRabbitQueue("dashboard.property-projection")
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.PropertyCreatedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.PropertyActivatedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.PropertyDeactivatedHandler))
            .AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.PropertyArchivedHandler));

        // CleaningOccurrenceRegistered has exactly one consumer (Dashboard)
        // in this process — not at risk, no sticky mapping needed.
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
        // ADR-020: ReservationCreated is shared with Housekeeping and
        // Dashboard (see their own queues above) — sticky-bind Workflow's
        // own single handler type here too, even though this queue only
        // ever needed one handler: without this, Wolverine's default
        // combining would still have pulled this queue's deliveries into
        // the shared chain along with the other two bounded contexts'
        // handlers, purely because they all discover a handler for the same
        // CLR message type.
        opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly);
        opts.ListenToRabbitQueue("workflow.reservation-created-trigger")
            .AddStickyHandler(typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler));

        // Communication's own single trigger consumer (Fase 9, Checkpoint 1):
        // a fourth, independent subscriber queue on the EXISTING
        // reservation-events exchange (Reservations never needs to know
        // Communication is listening — same decoupled pub/sub pattern as
        // Housekeeping's/Dashboard's/Workflow's own queues above).
        // Communication reacts DIRECTLY to ReservationCreated (choreography,
        // Fase 8's own registered criterion — never through Workflow
        // Orchestration). The queue itself, and its binding, is provisioned
        // exclusively by IHostPro.MigrationRunner (unconditionally, by
        // environment — topology provisioning is not gated, only whether
        // THIS process listens to it, see below). ReservationCreatedHandler
        // lives in Communication.Infrastructure, a separate assembly from
        // this entry assembly, so it must be explicitly included in
        // Wolverine's handler discovery — fully qualified below (never a
        // blanket `using`) for the same collision reason as Dashboard's/
        // Workflow's own ReservationCreatedHandler above.
        //
        // ADR-020: this is the fourth independent ReservationCreated
        // consumer sharing the message type with Housekeeping/Dashboard/
        // Workflow above (all three already sticky-bound) — without this
        // AddStickyHandler, Communication's own deliveries would be at the
        // same risk of Wolverine's default handler-chain-combining defect
        // that ADR-020 corrected for the other three.
        //
        // Gated to Development ONLY, mirroring the DI registration gate
        // above (AddCommunicationModule/AddCommunicationReservationConsumer)
        // exactly — same IsDevelopment() allowlist, same rationale: this
        // listener resolves a keyed handler that only exists in DI when
        // that gate is open. Binding the listener while gating only the DI
        // registration would let a non-Development process consume from
        // this queue with no handler registered for it — a runtime DI
        // resolution failure per message, not a clean absence. Keeping both
        // gates on the same condition means any non-Development process
        // never listens to this queue at all. Outside Development, the
        // corresponding queue/binding on the reservation-events exchange is
        // ALSO not provisioned by IHostPro.MigrationRunner (see its own
        // Program.cs, same IsDevelopment() condition) — a real
        // ReservationCreated published there never reaches a queue with no
        // consumer at all, closing the backlog risk that would otherwise
        // exist between CP1 (no real connector) and CP2 (real connector
        // activated): never a silent fake success, never a resolution
        // crash, never an accumulating backlog to replay retroactively.
        if (builder.Environment.IsDevelopment())
        {
            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Communication.Infrastructure.Messaging.ReservationCreatedHandler).Assembly);
            opts.ListenToRabbitQueue("communication.reservation-created-trigger")
                .AddStickyHandler(typeof(IHostPro.Contexts.Communication.Infrastructure.Messaging.ReservationCreatedHandler));
        }

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
