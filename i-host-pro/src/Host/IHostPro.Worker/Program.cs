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
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.GuestOperations.Infrastructure;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Payments.Infrastructure;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.Reservations.Contracts;
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

    // Fase 9, Checkpoint 3.2 ("Airbnb Deterministic Foundation"): the
    // Airbnb reservation import/update/cancel consumer — mirrors the
    // schedule-projection-consumer registration immediately above.
    // Unconditional (not gated to Development): unlike Communication's own
    // ReservationCreated consumer (which depends on a fake/real connector
    // distinction), this consumer's only external dependency is
    // ReservationsDbContext itself — there is no fake/real split to gate.
    builder.Services.AddReservationsAirbnbImportConsumer();

    // Fase 10, Checkpoint 1 (Guest Operations Foundation): the CloseReservation
    // cross-context command consumer — mirrors the Airbnb import consumer
    // registration immediately above. Sent exclusively by Workflow
    // Orchestration (see AddWorkflowModule below).
    builder.Services.AddReservationsCloseReservationCommand();

    // Fase 10, Checkpoint 3 (Early Check-in / Late Checkout): the two
    // RescheduleReservationForEarlyCheckIn/RescheduleReservationForLateCheckout
    // cross-context command consumers — mirrors AddReservationsCloseReservationCommand
    // immediately above. Sent exclusively by Workflow Orchestration's own
    // reschedule orchestrators (see AddWorkflowModule below).
    builder.Services.AddReservationsRescheduleCommands();

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

    // Property Management module (Fase 10, Checkpoint 4 — Portaria
    // Notification Foundation, ADR-026): Worker needs PropertyManagementDbContext
    // + IFrontDeskContactReader so Communication's new Front Desk processors
    // (registered below) can resolve the current front desk contact for a
    // Property. Read-only from this process — no command dispatch, no
    // writes, so (mirrors CommunicationDbContext's own precedent) no
    // EnrollAncillaryPostgresqlOutbox call is needed for
    // property_management_messaging here.
    builder.Services.AddPropertyManagementModule(builder.Configuration);

    // Communication module (Fase 9, Checkpoint 1): CommunicationDbContext +
    // its shared execution-scope/repository/transaction-executor DI graph
    // (ADR-016) — mirrors AddDashboardModule's own precedent. Fase 9,
    // Checkpoint 2.3.3 made this call UNCONDITIONAL (previously gated to
    // Development alongside the reservation consumer below): the new
    // WhatsApp status consumer registered right after it needs
    // CommunicationDbContext/IMessageRepository/ICommunicationTransactionExecutor
    // in every environment, not just Development — Communication now has
    // real, always-on work to do, not just the CP1 fake-connector demo.
    builder.Services.AddCommunicationModule(builder.Configuration);

    // Fase 9, Checkpoint 2.3.3 (ADR-022 item 14): WhatsAppMessageStatusChanged
    // consumer — unconditional, unlike AddCommunicationReservationConsumer
    // below. The inbound webhook status path has no fake/real connector
    // distinction of its own (External Integrations' webhook signature
    // verification is always real); gating this to Development would
    // silently drop real status updates in every other environment.
    builder.Services.AddCommunicationWhatsAppStatusConsumer();

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
        builder.Services.AddCommunicationReservationConsumer();

        // Fase 10, Checkpoint 4 (Portaria Notification Foundation): the
        // three Front Desk processors reuse the SAME FakeWhatsAppConnector
        // registered immediately above by AddCommunicationReservationConsumer
        // — same Development-only gate, same "zero real provider" reasoning.
        builder.Services.AddCommunicationFrontDeskConsumer();

        // Fase 10, Checkpoint 5 (PIX/Payment Deterministic Foundation): the
        // PIX-to-guest delivery processor reuses the SAME FakeWhatsAppConnector
        // registered above — same Development-only gate, same "zero real
        // provider" reasoning (PixChargeCreatedDeliveryProcessor depends on
        // IOutboundMessageConnector, unlike Payments' own PixCharge creation
        // path above, which is unconditional).
        builder.Services.AddCommunicationPixDeliveryConsumer();
    }

    // Fase 10, Checkpoint 2 (Check-in/Checkout Core): the ReservationCreated
    // choreography consumer that auto-creates GuestStayOperation — mirrors
    // AddReservationsModule + AddReservationsScheduleProjectionConsumer's own
    // two-call split immediately above. Unlike CP1 (zero Wolverine
    // consumers), Worker now touches GuestOperationsDbContext directly.
    builder.Services.AddGuestOperationsModule(builder.Configuration);
    builder.Services.AddGuestOperationsReservationCreatedConsumer();

    // Fase 10, Checkpoint 5 (PIX/Payment Deterministic Foundation): Guest
    // Operations' own reaction to Payments' PixChargeConfirmed — mirrors
    // AddGuestOperationsReservationCreatedConsumer's own placement exactly.
    builder.Services.AddGuestOperationsPixChargeConfirmedConsumer();

    // Payments module (Fase 10, Checkpoint 5 — PIX/Payment Deterministic
    // Foundation): DbContext + IPixChargeDeliveryReader (ADR-027, exception
    // #11 — needed by Communication's delivery processor below) +
    // LateCheckoutPaymentRequired/PixChargeConfirmationReceived consumers.
    // Unconditional (not gated to Development): unlike Communication's own
    // WhatsApp delivery, PixCharge creation/confirmation has no fake/real
    // provider distinction that would make it unsafe to run everywhere —
    // FakePixProvider IS this checkpoint's only provider, in every
    // environment (mirrors Guest Operations' own ReservationCreated
    // consumer reasoning above).
    builder.Services.AddPaymentsModule(builder.Configuration);
    builder.Services.AddPaymentsLateCheckoutPaymentRequiredConsumer();

    // FakePixProvider (Fase 10, Checkpoint 5 — ADR-025, synchronous
    // exception #10) — the ONLY IPixProvider implementation this
    // checkpoint has, registered unconditionally: no real provider exists
    // to gate against (unlike IMessagingProvider, which has a real,
    // Development-gated implementation alongside Communication's own fake).
    builder.Services.AddExternalIntegrationsPixProvider();

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

        // Guest Operations' own durable outbox (Fase 10, Checkpoint 1 —
        // enrolled only in IHostPro.Api until now; Checkpoint 2 makes this
        // the FIRST checkpoint Guest Operations consumes any message
        // in-process — ReservationCreatedGuestStayInitializer, reached via
        // IGuestOperationsMessageExecutionScope, needs
        // IDbContextOutbox<GuestOperationsDbContext>/IGuestOperationsTransactionExecutor
        // to resolve inside a Wolverine handler here too — same
        // empirically-confirmed requirement as Housekeeping's/Reservations'/
        // Dashboard's own).
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("GuestOperations")!,
            "guest_operations_messaging",
            typeof(GuestOperationsDbContext));

        // Payments' own durable outbox (Fase 10, Checkpoint 5 — PIX/Payment
        // Deterministic Foundation) — a ninth "ancillary" store, in its own
        // payments_messaging schema, never shared with any other context's.
        // PixChargeCreated/PixChargeConfirmed (routed below) are its
        // published Integration Events.
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Payments")!,
            "payments_messaging",
            typeof(PaymentsDbContext));

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

        // ADR-016, fifth application (Fase 10, Checkpoint 2) — same
        // rationale as Housekeeping's/Reservations'/Dashboard's/
        // Communication's own AlwaysUseServiceLocationFor above:
        // GuestOperationsMessageExecutionScope is the single, deliberately-
        // authorized place in Guest Operations that holds IServiceScopeFactory.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.GuestOperations.Application.IGuestOperationsMessageExecutionScope>();

        // ADR-016, sixth application (Fase 10, Checkpoint 5) — same rationale
        // as every other Bounded Context's own AlwaysUseServiceLocationFor
        // above: PaymentsMessageExecutionScope is the single, deliberately-
        // authorized place in Payments that holds IServiceScopeFactory.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Payments.Application.IPaymentsMessageExecutionScope>();

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

        // Fase 10, Checkpoint 1 (Guest Operations Foundation): Reservations'
        // consumed cross-context COMMAND, CloseReservation — same assembly
        // as CleaningCreatedHandler above, already included. The queue
        // itself, and its binding to the SAME workflow-orchestration-commands
        // exchange (new routing key, close_reservation), is provisioned
        // exclusively by IHostPro.MigrationRunner. Single consumer by design
        // (mirrors CreateCleaningForReservation) — not at risk, no sticky
        // mapping needed.
        opts.ListenToRabbitQueue("reservations.workflow-commands");

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

        // Fase 9, Checkpoint 3.2 ("Airbnb Deterministic Foundation"):
        // External Integrations' own Airbnb reservation events — a new,
        // independent subscriber queue on the EXISTING external-integrations-events
        // topic exchange (External Integrations never needs to know
        // Reservations is listening, same decoupled pub/sub pattern as every
        // queue above). The queue itself, and its bindings, are provisioned
        // exclusively by IHostPro.MigrationRunner.
        // AirbnbReservationImportedHandler/UpdatedHandler/CancelledHandler
        // live in the SAME Reservations.Infrastructure assembly as
        // CleaningCreatedHandler above — already included by the
        // opts.Discovery.IncludeAssembly(typeof(CleaningCreatedHandler).Assembly)
        // call above, no second IncludeAssembly needed.
        //
        // ADR-020: none of these three event types has a second in-process
        // consumer (confirmed: only Reservations consumes them) — no
        // AddStickyHandler needed, ADR-020's own "single discovered handler"
        // default applies.
        opts.ListenToRabbitQueue("reservations.airbnb-import");

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

        // Workflow Orchestration's second trigger consumer (Fase 10,
        // Checkpoint 1 — Guest Operations Foundation): a new, independent
        // subscriber queue on the NEW guest-operations-events exchange
        // (Guest Operations never needs to know Workflow is listening — same
        // decoupled pub/sub pattern as every queue above). The queue itself,
        // and its binding, is provisioned exclusively by
        // IHostPro.MigrationRunner. GuestCheckedOutHandler lives in the SAME
        // Workflow.Infrastructure assembly as ReservationCreatedHandler above
        // — already included, no second IncludeAssembly needed.
        // GuestCheckedOut has exactly one consumer in this process
        // (Workflow) — sticky-bound anyway, mirroring
        // ReservationCreatedHandler's own registration shape for
        // consistency (see GuestCheckedOutHandler's own doc comment).
        opts.ListenToRabbitQueue("workflow.guest-checked-out-trigger")
            .AddStickyHandler(typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.GuestCheckedOutHandler));

        // Workflow Orchestration's third trigger consumer (Fase 10,
        // Checkpoint 3 — Early Check-in / Late Checkout): a new, independent
        // subscriber queue on the NEW guest-operations-events exchange
        // (Guest Operations never needs to know Workflow is listening — same
        // decoupled pub/sub pattern as every queue above). The queue itself,
        // and its binding, is provisioned exclusively by
        // IHostPro.MigrationRunner. EarlyCheckinApprovedHandler lives in the
        // SAME Workflow.Infrastructure assembly as ReservationCreatedHandler/
        // GuestCheckedOutHandler above — already included, no second
        // IncludeAssembly needed. EarlyCheckinApproved has exactly one
        // consumer in this process (Workflow) — sticky-bound anyway,
        // mirroring GuestCheckedOutHandler's own registration shape for
        // consistency.
        opts.ListenToRabbitQueue("workflow.early-checkin-approved-trigger")
            .AddStickyHandler(typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.EarlyCheckinApprovedHandler));

        // Workflow Orchestration's fourth trigger consumer (Fase 10,
        // Checkpoint 3 — Early Check-in / Late Checkout): a new, independent
        // subscriber queue on the SAME guest-operations-events exchange.
        // Unlike EarlyCheckinApproved above, LateCheckoutApproved has a
        // SECOND, independent in-process consumer — Housekeeping's own
        // reaction (see housekeeping.late-checkout-approved-trigger below) —
        // so AddStickyHandler is mandatory here, not just
        // consistency-mirroring (ADR-020).
        opts.ListenToRabbitQueue("workflow.late-checkout-approved-trigger")
            .AddStickyHandler(typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.LateCheckoutApprovedHandler));

        // Housekeeping's own reaction to LateCheckoutApproved (Fase 10,
        // Checkpoint 3 — gated on UpdatesCleaning; ADR-020 second consumer
        // alongside Workflow's own queue immediately above): a SEPARATE
        // subscriber queue on the SAME guest-operations-events exchange —
        // Guest Operations never needs to know Housekeeping is listening,
        // same decoupled pub/sub pattern as every queue above. The queue
        // itself, and its binding, is provisioned exclusively by
        // IHostPro.MigrationRunner. Housekeeping's own LateCheckoutApprovedHandler
        // lives in the SAME Housekeeping.Infrastructure assembly as
        // PropertyCreatedHandler above — already included, no second
        // IncludeAssembly needed.
        opts.ListenToRabbitQueue("housekeeping.late-checkout-approved-trigger")
            .AddStickyHandler(typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Messaging.LateCheckoutApprovedHandler));

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

            // Fase 10, Checkpoint 4 (Portaria Notification Foundation): the
            // three Front Desk notification consumers on the EXISTING
            // guest-operations-events exchange — same Development-only gate
            // as the reservation-confirmation consumer immediately above
            // (same FakeWhatsAppConnector, same "no real provider yet"
            // reasoning). GuestCheckedIn gains its first-ever real consumer
            // here (previously published with zero queues bound).
            // EarlyCheckinApproved becomes a 2-consumer event (Workflow +
            // Communication); LateCheckoutApproved becomes a 3-consumer
            // event (Workflow + Housekeeping + Communication) — each in its
            // own sticky-bound queue (ADR-020), no competing consumers.
            opts.ListenToRabbitQueue("communication.guest-checked-in-trigger")
                .AddStickyHandler(typeof(IHostPro.Contexts.Communication.Infrastructure.Messaging.GuestCheckedInHandler));
            opts.ListenToRabbitQueue("communication.early-checkin-approved-trigger")
                .AddStickyHandler(typeof(IHostPro.Contexts.Communication.Infrastructure.Messaging.EarlyCheckinApprovedHandler));
            opts.ListenToRabbitQueue("communication.late-checkout-approved-trigger")
                .AddStickyHandler(typeof(IHostPro.Contexts.Communication.Infrastructure.Messaging.LateCheckoutApprovedHandler));

            // Fase 10, Checkpoint 5 (PIX/Payment Deterministic Foundation):
            // Communication's PIX-to-guest delivery consumer, on the NEW
            // payments-events exchange — same Development-only gate as
            // every other Communication consumer above (same
            // FakeWhatsAppConnector, same "no real provider yet" reasoning).
            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Communication.Infrastructure.Messaging.PixChargeCreatedHandler).Assembly);
            opts.ListenToRabbitQueue("communication.pixcharge-created-trigger")
                .AddStickyHandler(typeof(IHostPro.Contexts.Communication.Infrastructure.Messaging.PixChargeCreatedHandler));
        }

        // Guest Operations' own single trigger consumer (Fase 10, Checkpoint
        // 2 — Check-in/Checkout Core): a fifth, independent subscriber queue
        // on the EXISTING reservation-events exchange (Reservations never
        // needs to know Guest Operations is listening — same decoupled
        // pub/sub pattern as Housekeeping's/Dashboard's/Workflow's/
        // Communication's own queues above). Guest Operations reacts
        // DIRECTLY to ReservationCreated (choreography — the resolved
        // creation-trigger governance gate, never through Workflow
        // Orchestration) to auto-create its own GuestStayOperation. The
        // queue itself, and its binding, is provisioned exclusively by
        // IHostPro.MigrationRunner. ReservationCreatedGuestStayInitializer
        // lives in GuestOperations.Infrastructure, a separate assembly from
        // this entry assembly, so it must be explicitly included in
        // Wolverine's handler discovery — fully qualified below (never a
        // blanket `using`) for the same collision reason as Dashboard's/
        // Workflow's/Communication's own ReservationCreatedHandler above.
        //
        // ADR-020: this is the fifth independent ReservationCreated consumer
        // sharing the message type with Housekeeping/Dashboard/Workflow/
        // Communication above (all sticky-bound) — without this
        // AddStickyHandler, Guest Operations' own deliveries would be at the
        // same risk of Wolverine's default handler-chain-combining defect
        // ADR-020 corrected for the others.
        //
        // Unconditional (not gated to Development): unlike Communication's
        // own ReservationCreated consumer immediately above, this consumer
        // has no fake/real connector distinction — auto-creating a local
        // GuestStayOperation is always correct, in every environment.
        opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.GuestOperations.Infrastructure.Messaging.ReservationCreatedHandler).Assembly);
        opts.ListenToRabbitQueue("guestoperations.reservation-created-trigger")
            .AddStickyHandler(typeof(IHostPro.Contexts.GuestOperations.Infrastructure.Messaging.ReservationCreatedHandler));

        // Guest Operations' own reaction to Payments' PixChargeConfirmed
        // (Fase 10, Checkpoint 5 — PIX/Payment Deterministic Foundation): a
        // new, independent subscriber queue on the NEW payments-events
        // exchange (Payments never needs to know Guest Operations is
        // listening — same decoupled pub/sub pattern as every queue above).
        // PixChargeConfirmedHandler lives in the SAME GuestOperations.Infrastructure
        // assembly as ReservationCreatedHandler above — already included, no
        // second IncludeAssembly needed. PixChargeConfirmed has exactly one
        // consumer in this process (Guest Operations) — sticky-bound anyway,
        // mirroring every other handler's own registration shape for
        // consistency (ADR-020).
        opts.ListenToRabbitQueue("guestoperations.pixcharge-confirmed-trigger")
            .AddStickyHandler(typeof(IHostPro.Contexts.GuestOperations.Infrastructure.Messaging.PixChargeConfirmedHandler));

        // Payments' own two consumers (Fase 10, Checkpoint 5 — PIX/Payment
        // Deterministic Foundation). LateCheckoutPaymentRequiredHandler/
        // PixChargeConfirmationReceivedHandler live in
        // Payments.Infrastructure, a separate assembly from this entry
        // assembly, so it must be explicitly included in Wolverine's handler
        // discovery. Neither message type has a second in-process consumer
        // — no AddStickyHandler needed (ADR-020's own "single discovered
        // handler" default).
        opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Payments.Infrastructure.Messaging.LateCheckoutPaymentRequiredHandler).Assembly);

        // Payments' reaction to Guest Operations' own LateCheckoutPaymentRequired
        // — a new, independent subscriber queue on the EXISTING
        // guest-operations-events exchange (Guest Operations never needs to
        // know Payments is listening — same decoupled pub/sub pattern as
        // every queue above).
        opts.ListenToRabbitQueue("payments.late-checkout-payment-required-trigger");

        // Payments' inbound provider-neutral confirmation seam (Fase 10,
        // Checkpoint 5 mandate items 30/54): a new, independent, dedicated
        // Direct-exchange queue — see IHostPro.MigrationRunner's own
        // Program.cs for the "payments-commands" exchange declaration. This
        // checkpoint has no real PIX provider/webhook — the only publisher
        // today is the E2E test harness itself, simulating the
        // provider-neutral fact deterministically via a real Wolverine send;
        // the handler is genuine production code representing the seam a
        // future ExternalIntegrations webhook-normalization step would use.
        opts.ListenToRabbitQueue("payments.confirmation-received");

        // Fase 10, Checkpoint 5.1 (Payment Failure/Expiration Evidence
        // Corrective Gate): two more independent queues on the SAME
        // payments-commands Direct exchange — same provider-neutral seam,
        // same "no real provider/webhook yet, E2E test harness is the only
        // publisher today" reasoning as payments.confirmation-received
        // above. PixChargeFailureReceivedHandler/PixChargeExpirationReceivedHandler
        // live in the SAME Payments.Infrastructure assembly already included
        // via IncludeAssembly above — no second IncludeAssembly needed.
        opts.ListenToRabbitQueue("payments.failure-received");
        opts.ListenToRabbitQueue("payments.expiration-received");

        // Communication's WhatsApp status consumer (Fase 9, Checkpoint 2.3.3,
        // ADR-022 item 14) — a new, independent subscriber queue on the NEW
        // external-integrations-events exchange (External Integrations never
        // needs to know Communication is listening — same decoupled pub/sub
        // pattern as every queue above). The queue itself, and its binding,
        // is provisioned exclusively by IHostPro.MigrationRunner,
        // unconditionally (see its own Program.cs). Unconditional here too —
        // unlike the Development-gated ReservationCreated queue just above,
        // this consumer has no fake/real connector distinction of its own
        // (the signature-verified webhook that ultimately triggers this
        // event is always real), so gating it would silently drop real
        // status updates outside Development. WhatsAppMessageStatusChangedHandler
        // lives in the SAME Communication.Infrastructure assembly as
        // ReservationCreatedHandler above — IncludeAssembly is idempotent,
        // called here unconditionally so this handler is discovered even
        // when the Development-gated block above does not run.
        //
        // ADR-020: exactly one Wolverine-discovered handler class exists for
        // WhatsAppMessageStatusChanged in this process (Communication's own)
        // — confirmed by IHostPro.ArchitectureTests'
        // Exactly_One_Handler_Exists_For_WhatsAppMessageStatusChanged — so
        // no AddStickyHandler is needed (ADR-020's own "single discovered
        // handler" default).
        opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Communication.Infrastructure.Messaging.WhatsAppMessageStatusChangedHandler).Assembly);
        opts.ListenToRabbitQueue("communication.whatsapp-status-projection");

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

        // Fase 10, Checkpoint 1 (Guest Operations Foundation): Reservations'
        // own cross-context command, CloseReservation — same
        // workflow-orchestration-commands exchange (a second routing key,
        // never a second exchange), same Send-not-Publish semantics as
        // CreateCleaningForReservation above (see
        // GuestCheckedOutCloseReservationOrchestrator/WolverineWorkflowCommandDispatcher).
        opts.PublishMessage(typeof(CloseReservation))
            .ToRabbitRoutingKey(
                workflowOrchestrationCommandsExchange, "close_reservation",
                exchange => exchange.ExchangeType = ExchangeType.Direct)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Fase 10, Checkpoint 3 (Early Check-in / Late Checkout): Reservations'
        // own two new cross-context commands — same workflow-orchestration-commands
        // exchange (two more routing keys, never a second exchange), same
        // Send-not-Publish semantics as CloseReservation above (see
        // EarlyCheckinApprovedRescheduleOrchestrator/
        // LateCheckoutApprovedRescheduleOrchestrator/WolverineWorkflowCommandDispatcher).
        // Delivered to the SAME reservations.workflow-commands queue Reservations
        // already listens to above (new bindings only, provisioned exclusively by
        // IHostPro.MigrationRunner) — single consumer by design per command type,
        // not at risk, no sticky mapping needed.
        opts.PublishMessage(typeof(RescheduleReservationForEarlyCheckIn))
            .ToRabbitRoutingKey(
                workflowOrchestrationCommandsExchange, "reschedule_for_early_check_in",
                exchange => exchange.ExchangeType = ExchangeType.Direct)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        opts.PublishMessage(typeof(RescheduleReservationForLateCheckout))
            .ToRabbitRoutingKey(
                workflowOrchestrationCommandsExchange, "reschedule_for_late_checkout",
                exchange => exchange.ExchangeType = ExchangeType.Direct)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Payments' own two Integration Events (Fase 10, Checkpoint 5 —
        // PIX/Payment Deterministic Foundation), published by
        // LateCheckoutPaymentRequiredChargeInitializer/
        // PixChargeConfirmationReceivedCommandHandler — both run in THIS
        // process (unlike GuestOperations' own events, published from
        // IHostPro.Api where its HTTP command handlers run). A new, dedicated
        // Topic exchange — see IHostPro.MigrationRunner's own Program.cs for
        // the queue bindings.
        const string paymentsEventsExchange = "payments-events";
        opts.PublishMessage(typeof(IHostPro.Contexts.Payments.Contracts.PixChargeCreated))
            .ToRabbitRoutingKey(paymentsEventsExchange, "pix_charge_created", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        opts.PublishMessage(typeof(IHostPro.Contexts.Payments.Contracts.PixChargeConfirmed))
            .ToRabbitRoutingKey(paymentsEventsExchange, "pix_charge_confirmed", exchange => exchange.ExchangeType = ExchangeType.Topic)
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
