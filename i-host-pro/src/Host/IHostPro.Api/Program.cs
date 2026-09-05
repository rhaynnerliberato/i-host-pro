using IHostPro.Api.RateLimiting;
using IHostPro.Api.Security;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.BuildingBlocks.Infrastructure.RateLimiting;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Api.Swagger;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Infrastructure;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Infrastructure;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Infrastructure;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Infrastructure;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Api.Observability;
using JasperFx;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.With(new TraceContextEnricher())
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.With(new TraceContextEnricher())
        .WriteTo.Console());

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    // Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — an unhandled
    // exception previously fell through to Kestrel's bare framework default
    // (an empty 500 body) with no consistent logging and no ProblemDetails
    // shape, unlike every EXPECTED failure this Api already maps via
    // ResultHttpMapper. AddProblemDetails() + the UseExceptionHandler below
    // give unhandled exceptions the same response shape, and — critically —
    // guarantee the response body never includes the exception's own
    // Message/stack trace/inner exception, regardless of environment; only
    // a generic, fixed detail string is ever returned.
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<SanitizedExceptionHandler>();
    // Swashbuckle's default schemaId for generic types is derived only from
    // the type's own name and its generic arguments (e.g. Optional<string> ->
    // "StringOptional"), so identically-named generic types declared
    // independently in different bounded contexts collide (PropertyManagement
    // and Reservations each declare their own Optional<T>). Prefixing generic
    // schemaIds with their bounded-context namespace segment keeps ordinary
    // (non-generic) DTO schema names untouched while making generic
    // schemaIds collision-proof.
    builder.Services.AddSwaggerGen(options =>
    {
        options.CustomSchemaIds(SwaggerSchemaIdSelector);
        // Without this, Optional<T>'s public CLR shape (isSet/value) leaks into the
        // schema instead of the bare, nullable T that OptionalJsonConverter<T> actually
        // reads/writes on the wire — see OptionalSchemaFilter for the full rationale.
        options.SchemaFilter<OptionalSchemaFilter>();
        // OpenAPI operationId stability gate (Fase 6, Checkpoint 6) — see
        // SwaggerOperationIdSelector's own doc comment for the full defect
        // history and why this is scoped to only the two actions that need
        // it, rather than a global {Controller}_{Action} convention.
        options.CustomOperationIds(SwaggerOperationIdSelector);
    });

    // CORS for the Angular frontend (Fase 4, Incremento 1) — explicit origin
    // allowlist only, read from configuration ("Cors:AllowedOrigins"), never
    // a wildcard. No AllowCredentials(): the frontend authenticates with a
    // Bearer token attached manually to each request, never a cookie, so the
    // browser's credentialed-request mode is not needed here.
    //
    // Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — CorsOriginsResolver
    // fails fast in Production instead of silently falling back to the dev
    // default; see its own doc comment for the full rationale and for why
    // the rule lives there rather than inline here.
    const string FrontendCorsPolicy = "Frontend";
    var allowedOrigins = CorsOriginsResolver.ResolveAllowedOrigins(builder.Configuration, builder.Environment.IsProduction());
    builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

    // Fase 12, Checkpoint 4 — X-Forwarded-For/X-Forwarded-Proto support,
    // configurable so CP5 can name the real reverse proxy once its
    // infrastructure exists. ASP.NET Core's own default (KnownNetworks =
    // 127.0.0.0/8, KnownProxies = ::1) already means "trust nothing except a
    // proxy on this same machine" — never cleared here, only ever added to,
    // so this never regresses to "trust any forwarded source" (the exact
    // spoofing risk AuthController's own doc comment already calls out).
    // With no "ForwardedHeaders:*" configuration (today, in every
    // environment), behavior is unchanged from before this checkpoint.
    var forwardedHeadersOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
    };
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        forwardedHeadersOptions.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        forwardedHeadersOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));

    // Multi-tenant: resolved per request by an authentication/authorization
    // middleware once login/JWT exist (Incremento 2). The scoped instance is
    // registered here so every downstream service in the request can depend
    // on ITenantContext (Architecture Principles, Section 7).
    builder.Services.AddScoped<ITenantContext, TenantContext>();

    // Read-only accessor Application-layer handlers use to get the resolved
    // tenant id without depending on ITenantContext directly (Application
    // cannot reference BuildingBlocks.Infrastructure) — Incremento 2 plan,
    // Etapa 9.
    builder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();

    // Fase 12, Checkpoint 2 (Observability Finalization) — real dependency
    // checks (Documento 21 §18), replacing the bare AddHealthChecks() that
    // reported "Healthy" regardless of whether Postgres/RabbitMQ/Redis were
    // actually reachable. Tagged "ready" so /health/ready (mapped after
    // app.Build() below) reflects genuine readiness; /health/live never
    // touches any dependency at all. Postgres and RabbitMQ are hard
    // dependencies for every write path this Api serves — Unhealthy when
    // down. Redis backs only Configuration & Policy's read-through cache
    // (RedisPolicyValueCache falls back to PostgreSQL on any Redis failure —
    // corrected during CP2's own final validation, see the Fase 12
    // homologation document §4.8), so only a caching optimization is lost,
    // never a whole read path — reported as Degraded, never Unhealthy, so a
    // Redis blip alone never flips readiness off entirely.
    // CP5.3D-B2 corrective Decision Gate: this factory must go through the
    // exact same ApplyIHostProRabbitMqSettings helper UseIHostProRabbitMq
    // uses below, or it silently diverges on TLS/port - which it did,
    // deterministically failing this health check against Amazon MQ's
    // TLS-only endpoint (always attempting the client library's default
    // plaintext port 5672) while Wolverine's own connection worked fine.
    var rabbitMqConnectionForHealth = new Lazy<Task<RabbitMQ.Client.IConnection>>(() =>
    {
        var factory = new RabbitMQ.Client.ConnectionFactory();
        factory.ApplyIHostProRabbitMqSettings(builder.Configuration);
        return factory.CreateConnectionAsync();
    });

    builder.Services.AddHealthChecks()
        .AddNpgSql(
            builder.Configuration.GetConnectionString("Platform")
                ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Platform'."),
            name: "postgres",
            tags: ["ready"])
        .AddRabbitMQ(_ => rabbitMqConnectionForHealth.Value, name: "rabbitmq", tags: ["ready"])
        .AddRedis(
            builder.Configuration["Configuration:PolicyCache:ConnectionString"]
                ?? throw new InvalidOperationException("Missing configuration 'Configuration:PolicyCache:ConnectionString'."),
            name: "redis",
            failureStatus: HealthStatus.Degraded,
            tags: ["ready"]);

    // Fase 12, Checkpoint 5.3E (Observability Architecture) — publishes the
    // "ready"-tagged health checks above as an OTel gauge, closing the
    // signal gap for the Postgres/RabbitMQ/Redis alerts in the already-
    // approved catalogue (Fase 12 §4.6). See the class doc comment.
    builder.Services.AddHostedService<DependencyHealthMetricsBackgroundService>();

    // Fase 12, Checkpoint 3 (Resilience & Rate Limiting) — Redis-backed,
    // centrally-configured rate limiting (ADR-006 already names Redis for
    // this). AddIHostProRateLimiting registers the host-agnostic
    // IDistributedRateLimiter (shared with IHostPro.Worker's own AI
    // cost-guard policy); AddIHostProHttpRateLimiting wires the three
    // HTTP-native categories onto it (see ApiRateLimitingExtensions'
    // doc comment — Webhook is deliberately excluded here, see there).
    builder.Services.AddIHostProRateLimiting(builder.Configuration);
    builder.Services.AddIHostProHttpRateLimiting();

    // Tenant-aware transactional pipeline (TenantTransactionBehavior /
    // TenantBootstrapBehavior + ITenantAwareUnitOfWork) — foundation
    // registered now; no Command/Query dispatches through it yet, since no
    // handler exists until Incremento 2 (Incremento 1 plan, Section 12).
    builder.Services.AddIHostProTenantAwarePipeline();

    // Identity & Access module (Incremento 1 plan) — DbContext, custom
    // Identity stores/hasher/validator, tenant bootstrap reader. Incremento 2
    // (login, JWT, refresh token, logout) is being added incrementally on top
    // of this foundation. isDevelopmentEnvironment gates the Development-only
    // tenant/user seed configuration (Incremento 2 plan, ajuste 3-4).
    builder.Services.AddIdentityModule(builder.Configuration, builder.Environment.IsDevelopment());

    // JWT access-token issuance (RSA signing key + IJwtTokenGenerator) — kept
    // deliberately separate from AddIdentityModule and registered ONLY here,
    // never in IHostPro.Worker's Program.cs: the Worker never issues or
    // validates a JWT and must never be required to hold the signing private
    // key (Incremento 2 plan, Etapa 6).
    builder.Services.AddIdentityJwtIssuance(builder.Configuration);

    // Session-revocation cache acceleration (Incremento 2 plan, Etapa 12) —
    // Redis-backed ISessionRevocationCache, registered ONLY here, never in
    // IHostPro.Worker's Program.cs (mirrors AddIdentityJwtIssuance above):
    // AddIdentityModule already registers a harmless no-op default for both
    // hosts, this overrides it with the real implementation for the Api.
    builder.Services.AddIdentitySessionRevocationCache(builder.Configuration);

    // JWT Bearer authentication/authorization (Incremento 2 plan, Etapa 13;
    // ADR-012) — validates the access tokens AddIdentityJwtIssuance's
    // IJwtTokenGenerator issues, and consults AddIdentitySessionRevocationCache's
    // cache to fast-reject an already-revoked session. Registered ONLY here,
    // never in IHostPro.Worker's Program.cs, for the same reason as both of
    // the registrations above. Must be called after them (see
    // AddIdentityJwtBearerAuthentication's own doc comment).
    builder.Services.AddIdentityJwtBearerAuthentication();

    // Permission-code authorization policies (Incremento 3 plan, Checkpoint
    // 1) — USERS:MANAGE, ROLES:READ, PERMISSIONS:READ. No IAuthorizationHandler
    // is registered for PermissionRequirement yet (Checkpoint 2); no endpoint
    // references these policies yet either (Checkpoints 3+), so this is
    // inert until then. Registered here, never in IHostPro.Worker's
    // Program.cs, for the same reason as JWT Bearer authentication above:
    // the Worker never serves HTTP requests.
    builder.Services.AddIdentityAuthorization();

    // Mediator + the three auth commands' handlers/validators/pipeline
    // behaviors (Incremento 2 plan, Etapa 14) — registered ONLY here, never
    // in IHostPro.Worker's Program.cs: dispatching these commands is an
    // HTTP-request concern. AuthController (IHostPro.Contexts.Identity.Api,
    // discovered by AddControllers() below via the project reference) only
    // ever calls ISender.Send(...) — never a concrete handler.
    builder.Services.AddIdentityCommandDispatch();

    // Property Management module (Fase 2, Incremento 1, Checkpoint 1) —
    // DbContext registration.
    builder.Services.AddPropertyManagementModule(builder.Configuration, builder.Environment.IsDevelopment());

    // Property Management's Commands/Queries/handlers/validators/pipeline
    // behaviors (Fase 2, Incremento 1, Checkpoint 2) — mirrors
    // AddIdentityCommandDispatch's placement exactly: dispatching a
    // Command/Query is an HTTP-request concern, never registered in
    // IHostPro.Worker's Program.cs.
    builder.Services.AddPropertyManagementCommandDispatch();

    // Reservations module (Fase 3, Incremento 1) — DbContext registration.
    // Reservations.Application references PropertyManagement.Contracts only
    // (IPropertyReservationEligibilityReader) — never PropertyManagement.Application/
    // Infrastructure/Api.
    builder.Services.AddReservationsModule(builder.Configuration);

    // Reservations' Commands/Queries/handlers/validators/pipeline behaviors
    // (Fase 3, Incremento 1) — mirrors AddPropertyManagementCommandDispatch's
    // placement exactly: dispatching a Command/Query is an HTTP-request
    // concern, never registered in IHostPro.Worker's Program.cs.
    builder.Services.AddReservationsCommandDispatch();

    // Configuration & Policy module — DbContext, resolver and typed readers
    // registration. Registered here (never IHostPro.Worker's Program.cs)
    // mirroring every other context's own module registration placement.
    builder.Services.AddConfigurationModule(builder.Configuration);

    // Configuration & Policy's Commands/Queries/handlers/validators/pipeline
    // behaviors (Checkpoint 4) — mirrors AddReservationsCommandDispatch's
    // placement exactly; calls AddConfigurationApplicationMediator()
    // internally.
    builder.Services.AddConfigurationCommandDispatch();

    // Housekeeping module (Fase 6, Incremento 1) — DbContext + the parts
    // IHostPro.Worker also needs (audit writer, event collector, executor,
    // local Property/Reservation projections and their Wolverine consumers)
    // — see HousekeepingModuleExtensions' own doc comment for why this
    // differs from Configuration & Policy's Worker-only-needs-the-cache
    // shape.
    builder.Services.AddHousekeepingModule(builder.Configuration);

    // Housekeeping's Commands/Queries/handlers/validators/pipeline behaviors
    // — mirrors AddReservationsCommandDispatch's placement exactly:
    // dispatching a Command/Query is an HTTP-request concern, never
    // registered in IHostPro.Worker's Program.cs.
    builder.Services.AddHousekeepingCommandDispatch();

    // Dashboard & Reporting module (Fase 7, Incremento 2, Checkpoint 2) —
    // DbContext registration. IHostPro.Api never registered this before
    // Checkpoint 2 (Dashboard had no HTTP surface until the Overview query).
    builder.Services.AddDashboardModule(builder.Configuration);

    // Dashboard's Commands/Queries/handlers/validators/pipeline behaviors —
    // mirrors AddReservationsCommandDispatch's placement exactly: dispatching
    // a Command/Query is an HTTP-request concern, never registered in
    // IHostPro.Worker's Program.cs.
    builder.Services.AddDashboardQueryDispatch();

    // External Integrations module (Fase 9, Checkpoint 2.1 — foundation
    // only). DbContext registration is unconditional in every environment
    // (schema/config is not an external side-effect); the Development-only
    // credential provider is gated inside AddExternalIntegrationsModule
    // itself. IHostPro.Worker never registers this checkpoint — the
    // administrative configuration API is this module's only consumer so
    // far (CP2.1 mandate §16/§20: real outbound wiring belongs to CP2.2).
    builder.Services.AddExternalIntegrationsModule(builder.Configuration, builder.Environment.IsDevelopment());
    builder.Services.AddExternalIntegrationsCommandDispatch();

    // Guest Operations module (Fase 10, Checkpoint 1 — Guest Operations
    // Foundation; Checkpoint 2 — Check-in/Checkout Core). DbContext +
    // Mediator-dispatched check-in/checkout commands. GuestStayOperationsController's
    // two endpoints are this module's first real HTTP surface (CP1 shipped
    // zero endpoints).
    //
    // Fase 11, Checkpoint 4: the Command Mediator wiring (previously a
    // separate Api-only AddGuestOperationsCommandDispatch call) moved inside
    // AddGuestOperationsModule itself, so the AI Agent's own Worker-hosted
    // write Tools can reach it too (Exception #3) — see
    // GuestOperationsModuleExtensions' own doc comment. Nothing Api-only was
    // left to keep in a separate method.
    builder.Services.AddGuestOperationsModule(builder.Configuration);

    // Fase 10, Checkpoint 1: ICloseReservationHandler is also resolved
    // directly from this host by the deterministic E2E test's own
    // idempotency check (mirrors ICreateCleaningForReservationHandler's own
    // precedent) — Reservations' outbox is already enrolled in this process
    // (ReservationCreated), so no new Wolverine wiring is needed here.
    builder.Services.AddReservationsCloseReservationCommand();

    // Fase 10, Checkpoint 3: IRescheduleReservationForEarlyCheckInHandler/
    // IRescheduleReservationForLateCheckoutHandler are also resolved directly
    // from this host by their own deterministic E2E tests' idempotency
    // checks — mirrors AddReservationsCloseReservationCommand immediately
    // above exactly.
    builder.Services.AddReservationsRescheduleCommands();

    // AI Agent's own write-Command surface (Fase 11, Checkpoint 6 — Human
    // Handoff, Safety & Audit). ResumeAgentSessionCommand is AIAgent's first
    // HTTP-triggered write and this process's first ever reference to the
    // AI Agent Bounded Context — IHostPro.Worker never calls this method
    // (Resume has no in-process consumer there). Deliberately NOT
    // AddAIAgentModule: that method also registers every Read/Write Tool,
    // each needing another Bounded Context's own request dispatcher this
    // process does not uniformly compose (Payments/Communication have no
    // Api-hosted module here) — see AIAgentCommandDispatchExtensions' own
    // doc comment.
    builder.Services.AddAIAgentCommandDispatch(builder.Configuration);

    // Communication module (Fase 11, Checkpoint 6) — this process's first
    // ever reference to Communication, needed only for the new
    // administrator-notification-contact management endpoints
    // (Get/UpsertAdministratorNotificationContact). AddCommunicationModule
    // also registers Communication's own Mediator unconditionally
    // (SendAgentResponseCommand/SendHumanHandoffNotificationCommand, both
    // needing IOutboundMessageConnector, which this process never
    // registers) — KeepOnlyMediatorHandlers keeps only the two handlers this
    // host actually calls, mirroring the exact same discipline
    // IHostPro.Worker applies in reverse.
    builder.Services.AddCommunicationModule(builder.Configuration);
    builder.Services.KeepOnlyMediatorHandlers(
        typeof(UpsertAdministratorNotificationContactCommandHandler), typeof(GetAdministratorNotificationContactQueryHandler));

    // Wolverine's own Main message store (Fase 2, Incremento 1, Checkpoint 6
    // homologação — found and fixed during real-host startup validation):
    // Identity's and Property Management's outboxes are both registered as
    // MessageStoreRole.Ancillary (EnrollAncillaryPostgresqlOutbox below), and
    // Wolverine requires exactly one store designated MessageStoreRole.Main
    // whenever any Ancillary store exists — with zero Main store, hosting
    // fails to start with InvalidWolverineStorageConfigurationException
    // ("...none has been designated as the 'Main' store"). platform_messaging
    // is deliberately NOT a Bounded Context and never carries a domain event:
    // it exists purely to satisfy Wolverine's own runtime coordination
    // (node/agent registration, scheduled-job locking) — no DbContext is ever
    // .Enroll()-ed into it, so no Integration Event can ever be routed here by
    // construction. Provisioned exclusively by IHostPro.MigrationRunner
    // (Weasel resource setup, ihostpro_migrator role), never by this process
    // (AutoBuildMessageStorageOnStartup = AutoCreate.None below still applies
    // to every store, Main included).
    var platformMessagingConnectionString = builder.Configuration.GetConnectionString("Platform")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Platform'.");

    // IHostPro.Api only publishes Integration Events (via IEventPublisher); it never
    // consumes messages — consumers/handlers live exclusively in IHostPro.Worker
    // (Architecture Principles, Section 2). "listen: false" means this process
    // never creates receive queues (sender-only connection).
    builder.Host.UseWolverine(opts =>
    {
        opts.UseIHostProRabbitMq(builder.Configuration, listen: false);

        // MessageStoreRole defaults to Main — deliberately not passed
        // explicitly here, so this reads the same way PersistMessagesWithPostgresql
        // is documented (Wolverine.Postgresql 6.22.0): the default role IS Main.
        opts.PersistMessagesWithPostgresql(platformMessagingConnectionString, "platform_messaging");

        // Identity's own durable outbox (Incremento 2 plan, Etapa 15A; ADR-004)
        // — an "ancillary" store enrolled only to IdentityDbContext, in its own
        // identity_messaging schema, never a store shared with any future
        // Bounded Context. Registered ONLY here, never in IHostPro.Worker's
        // Program.cs: Worker gets no access to Identity's store/credentials
        // this increment (no consumer of these events exists yet).
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Identity")!,
            "identity_messaging",
            typeof(IdentityDbContext));

        // Required in addition to EnrollAncillaryPostgresqlOutbox above — confirmed
        // empirically (Incremento 2 plan, Etapa 15A): without this,
        // IDbContextOutbox<IdentityDbContext> never gets registered by Wolverine's
        // DI wiring at all, and every constructor depending on it (including
        // IdentityOutboxTransactionExecutor) fails to resolve.
        opts.UseEntityFrameworkCoreTransactions();

        // No host may create/alter the outbox's schema/tables at runtime —
        // only IHostPro.MigrationRunner may, via Wolverine/Weasel's own
        // resource management (host.SetupResources()), never a plain EF Core
        // migration and never UseResourceSetupOnStartup.
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        // Property Management's own durable outbox (Fase 2, Incremento 1,
        // Checkpoint 1 plan, item 6) — an "ancillary" store enrolled only to
        // PropertyManagementDbContext, in its own property_management_messaging
        // schema, never shared with identity_messaging. No route is
        // registered here yet — no Integration Event exists in this context
        // until a later checkpoint publishes its first one.
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("PropertyManagement")!,
            "property_management_messaging",
            typeof(PropertyManagementDbContext));

        // Reservations' own durable outbox (Fase 3, Incremento 1 plan) — a
        // third "ancillary" store, in its own reservations_messaging schema,
        // never shared with identity_messaging/property_management_messaging.
        // Applies the Fase 2, Checkpoint 6 fix (MapWolverineEnvelopeStorage +
        // MessageContext.OverrideStorage) from this context's very first
        // checkpoint — see ReservationsDbContext/ReservationsOutboxTransactionExecutor's
        // own doc comments.
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Reservations")!,
            "reservations_messaging",
            typeof(ReservationsDbContext));

        // Configuration & Policy's own durable outbox (Fase 5, Incremento 1
        // — Policy Engine Foundation, Checkpoint 1) — a fourth "ancillary"
        // store, in its own configuration_messaging schema, never shared
        // with any other context's. No route is registered here yet —
        // PolicyUpdated is the first event this context publishes, added
        // only in Checkpoint 6 — mirrors Property Management's own
        // Checkpoint 1 precedent (schema provisioned ahead of its first
        // real event).
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Configuration")!,
            "configuration_messaging",
            typeof(ConfigurationDbContext));

        // Housekeeping's own durable outbox (Fase 6, Incremento 1) — a
        // fifth "ancillary" store, in its own housekeeping_messaging schema,
        // never shared with any other context's.
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Housekeeping")!,
            "housekeeping_messaging",
            typeof(HousekeepingDbContext));

        // External Integrations' own durable outbox (Fase 9, Checkpoint
        // 2.3.3, ADR-022 item 13) — a sixth "ancillary" store, in its own
        // external_integrations_messaging schema, never shared with any
        // other context's. WhatsAppMessageStatusChanged (routed below) is
        // its first published Integration Event.
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("ExternalIntegrations")!,
            "external_integrations_messaging",
            typeof(ExternalIntegrationsDbContext));

        // Guest Operations' own durable outbox (Fase 10, Checkpoint 1 —
        // Guest Operations Foundation) — a seventh "ancillary" store, in its
        // own guest_operations_messaging schema, never shared with any other
        // context's. GuestCheckedOut/GuestCheckedIn (routed below) are its
        // published Integration Events. Enrolled here for HTTP-triggered
        // check-in/checkout (Checkpoint 2's two endpoints, this process) —
        // ALSO enrolled in IHostPro.Worker's own Program.cs since Checkpoint
        // 2 additionally makes Guest Operations a real in-process Wolverine
        // consumer (ReservationCreatedGuestStayInitializer, a different
        // physical process, same database).
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("GuestOperations")!,
            "guest_operations_messaging",
            typeof(GuestOperationsDbContext));

        // AI Agent's own durable outbox (Fase 11, Checkpoint 6 — Human
        // Handoff, Safety & Audit) — an eighth "ancillary" store, in its own
        // ai_agent_messaging schema, never shared with any other context's.
        // Enrolled here for the first time because ResumeAgentSessionCommand
        // is AIAgent's first HTTP-triggered write — its own
        // IAIAgentTransactionExecutor resolves IDbContextOutbox<AIAgentDbContext>
        // the same empirically-confirmed way every other write-capable
        // Bounded Context needs, mirroring GuestOperations' own precedent
        // immediately above. AI Agent publishes no Integration Event of its
        // own (mandate item 29 from Checkpoint 2, still true), so no route is
        // ever registered against this store — it exists purely so the
        // outbox type resolves inside this process's own DI container.
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("AIAgent")!,
            "ai_agent_messaging",
            typeof(AIAgentDbContext));

        // Communication's own durable outbox (Fase 9, Checkpoint 1) — a
        // ninth "ancillary" store, in its own communication_messaging
        // schema. Enrolled here for the first time (Fase 11, Checkpoint 6)
        // because the new administrator-notification-contact management
        // endpoints are this process's first reference to Communication —
        // ICommunicationTransactionExecutor resolves IDbContextOutbox<CommunicationDbContext>
        // the same way every other write-capable Bounded Context needs.
        opts.EnrollAncillaryPostgresqlOutbox(
            builder.Configuration.GetConnectionString("Communication")!,
            "communication_messaging",
            typeof(CommunicationDbContext));

        // Identity & Access's first six Integration Events (Incremento 2 plan,
        // Etapa 15; Documento 07 §13.2; ADR-013): one topic exchange per
        // Bounded Context, routing key = event name in snake_case.
        // .UseDurableOutbox() is required on every route — confirmed
        // empirically in Etapa 15A that without it, Wolverine defaults to the
        // "Inline" sending mode, which does not use the persistent outbox: a
        // broker outage makes it retry a few times in-process and then
        // discard the message, never falling back to durable persist-and-relay.
        //
        // .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1) — confirmed
        // by decompiling Wolverine 6.22.0 (homologação real, Incremento 2) that
        // DurableSendingAgent's first delivery attempt after commit is
        // synchronous and awaited by the caller; on failure it retries inline,
        // awaited, up to Endpoint.FailuresBeforeCircuitBreaks times (default 3)
        // before latching the circuit and deferring the rest to the background
        // Durability Agent. This is Wolverine's own official, public
        // configuration surface (SubscriberConfiguration.CircuitBreaking) for
        // that exact behavior — not a custom workaround — and caps the
        // request's exposure to a single synchronous attempt instead of three.
        const string identityEventsExchange = "identity-events";

        void RouteIdentityEvent<TEvent>(string routingKey) where TEvent : IntegrationEvent =>
            opts.PublishMessage(typeof(TEvent))
                .ToRabbitRoutingKey(identityEventsExchange, routingKey, exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox()
                .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        RouteIdentityEvent<UserLoggedIn>("user_logged_in");
        RouteIdentityEvent<LoginFailed>("login_failed");
        RouteIdentityEvent<AccountLockedOut>("account_locked_out");
        RouteIdentityEvent<UserLoggedOut>("user_logged_out");
        RouteIdentityEvent<RefreshTokenReuseDetected>("refresh_token_reuse_detected");
        RouteIdentityEvent<SessionRevoked>("session_revoked");

        // Incremento 3, Checkpoint 5: CreateUserCommand is the first command
        // to publish these two (Documento 07 §13.3/§13.4) — registered only
        // now that real code actually emits them, never in advance.
        RouteIdentityEvent<UserCreated>("user_created");
        RouteIdentityEvent<UserRoleAssigned>("user_role_assigned");

        // Incremento 3, Checkpoint 6: RemoveRoleCommand is the first command
        // to publish UserRoleRemoved (Documento 07 §13.3/§13.4).
        // UserRoleAssigned above is now ALSO published by AssignRoleCommand,
        // reusing the same route — never registered twice.
        RouteIdentityEvent<UserRoleRemoved>("user_role_removed");

        // Incremento 3, Checkpoint 7: BlockUserCommand/UnblockUserCommand are
        // the first commands to publish these two (Documento 07 §13.3/§13.4).
        RouteIdentityEvent<UserBlocked>("user_blocked");
        RouteIdentityEvent<UserUnblocked>("user_unblocked");

        // Incremento 3, Checkpoint 8: UpdateUserCommand is the first command
        // to publish this one (Documento 07 §13.3/§13.4).
        RouteIdentityEvent<UserUpdated>("user_updated");

        // Incremento 3, Checkpoint 9: ChangeOwnPasswordCommand/AdminResetPasswordCommand
        // are the first commands to publish this one (Documento 07 §13.3/§13.4)
        // — both reuse the same route, never registered twice.
        RouteIdentityEvent<PasswordChanged>("password_changed");

        // Property Management's first Integration Events (Fase 2, Incremento
        // 1, Checkpoint 2 plan, item 11) — its own topic exchange, never
        // identity-events. CreateCondominiumCommand/UpdateCondominiumCommand
        // are the first commands to publish these two.
        const string propertyManagementEventsExchange = "property-management-events";

        void RoutePropertyManagementEvent<TEvent>(string routingKey) where TEvent : IntegrationEvent =>
            opts.PublishMessage(typeof(TEvent))
                .ToRabbitRoutingKey(propertyManagementEventsExchange, routingKey, exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox()
                .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        RoutePropertyManagementEvent<CondominiumCreated>("condominium_created");
        RoutePropertyManagementEvent<CondominiumUpdated>("condominium_updated");

        // Property's own Integration Events (Fase 2, Incremento 1, Checkpoint
        // 3 plan, item 11) — same exchange.
        RoutePropertyManagementEvent<PropertyCreated>("property_created");
        RoutePropertyManagementEvent<PropertyUpdated>("property_updated");

        // Property's lifecycle Integration Events (Fase 2, Incremento 1,
        // Checkpoint 4 plan, item 11) — same exchange, same shared
        // CircuitBreaking(FailuresBeforeCircuitBreaks = 1).
        RoutePropertyManagementEvent<PropertyActivated>("property_activated");
        RoutePropertyManagementEvent<PropertyDeactivated>("property_deactivated");
        RoutePropertyManagementEvent<PropertyArchived>("property_archived");

        // Property Ownership's Integration Events (Fase 2, Incremento 1,
        // Checkpoint 5 plan, item 15) — same exchange, same shared
        // CircuitBreaking(FailuresBeforeCircuitBreaks = 1).
        RoutePropertyManagementEvent<PropertyOwnerLinked>("property_owner_linked");
        RoutePropertyManagementEvent<PropertyOwnerUnlinked>("property_owner_unlinked");

        // Reservations' first Integration Events (Fase 3, Incremento 1 plan,
        // item 12) — its own topic exchange, never identity-events/
        // property-management-events.
        const string reservationEventsExchange = "reservation-events";

        void RouteReservationEvent<TEvent>(string routingKey) where TEvent : IntegrationEvent =>
            opts.PublishMessage(typeof(TEvent))
                .ToRabbitRoutingKey(reservationEventsExchange, routingKey, exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox()
                .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        RouteReservationEvent<ReservationCreated>("reservation_created");
        RouteReservationEvent<ReservationUpdated>("reservation_updated");
        RouteReservationEvent<ReservationCancelled>("reservation_cancelled");

        // Configuration & Policy's first Integration Event (Fase 5, Incremento
        // 1, Checkpoint 6) — its own topic exchange, declared since Checkpoint
        // 1 (IHostPro.MigrationRunner), never identity-events/property-management-events/
        // reservation-events. See Documento 07 §28.
        const string configurationEventsExchange = "configuration-events";

        void RouteConfigurationEvent<TEvent>(string routingKey) where TEvent : IntegrationEvent =>
            opts.PublishMessage(typeof(TEvent))
                .ToRabbitRoutingKey(configurationEventsExchange, routingKey, exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox()
                .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        RouteConfigurationEvent<PolicyUpdated>("policy_updated");

        // Housekeeping's Integration Events (Fase 6, Incremento 1 plan) —
        // its own topic exchange, never any other context's.
        //
        // Fase 7, Incremento 1 (Agenda Foundation, Checkpoint 1 closure)
        // real defect found and fixed: this comment previously claimed
        // CleaningNeedsHelp/CleaningNeedsMaterial were "deliberately NOT
        // routed here... never published by this increment's administrative
        // lifecycle" — true when written (Fase 6, Incremento 1), but
        // Incremento 2A's Portal da Faxineira DID add real command handlers
        // (MarkCleaningWaitingHelpCommandHandler/MarkOwnCleaningWaitingHelpCommandHandler
        // and their Material counterparts) that stage these events in
        // Housekeeping's own outbox — with no matching route, they were
        // staged but never actually delivered to RabbitMQ. Both are routed
        // below now. CleaningInTransit/CleaningInterrupted are new events
        // (Checkpoint 1 closure, approved) for the two remaining real
        // Cleaning.Status transitions that previously published nothing at
        // all. CleaningDelayed remains deliberately NOT routed — it carries
        // no field any real consumer displays and corresponds to no
        // Cleaning.Status transition (ReportOwnCleaningDelayCommandHandler
        // never calls a Cleaning transition method).
        const string housekeepingEventsExchange = "housekeeping-events";

        void RouteHousekeepingEvent<TEvent>(string routingKey) where TEvent : IntegrationEvent =>
            opts.PublishMessage(typeof(TEvent))
                .ToRabbitRoutingKey(housekeepingEventsExchange, routingKey, exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox()
                .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        RouteHousekeepingEvent<CleaningCreated>("cleaning_created");
        RouteHousekeepingEvent<CleaningAssigned>("cleaning_assigned");
        RouteHousekeepingEvent<CleaningInTransit>("cleaning_in_transit");
        RouteHousekeepingEvent<CleaningStarted>("cleaning_started");
        RouteHousekeepingEvent<CleaningInspectionStarted>("cleaning_inspection_started");
        RouteHousekeepingEvent<CleaningCompleted>("cleaning_completed");
        RouteHousekeepingEvent<CleaningInterrupted>("cleaning_interrupted");
        RouteHousekeepingEvent<CleaningNeedsHelp>("cleaning_needs_help");
        RouteHousekeepingEvent<CleaningNeedsMaterial>("cleaning_needs_material");
        RouteHousekeepingEvent<CleaningCancelled>("cleaning_cancelled");

        // Fase 7, Incremento 2 (Dashboard & Reporting Foundation), Checkpoint
        // 0/1 decision 3 — RegisterCleaningOccurrenceCommandHandler's own
        // new event, Dashboard's initial (and, this increment, only)
        // consumer.
        RouteHousekeepingEvent<CleaningOccurrenceRegistered>("cleaning_occurrence_registered");

        // External Integrations' first Integration Event (Fase 9, Checkpoint
        // 2.3.3, ADR-022 item 13/14) — its own topic exchange, published by
        // WhatsAppWebhookStatusEventPublisher after signature verification,
        // tenant routing, and status normalization. Unconditional in every
        // environment — unlike the outbound send path's Development-only
        // credential/connector gates, the webhook's inbound status ingestion
        // has no fake/real distinction of its own.
        const string externalIntegrationsEventsExchange = "external-integrations-events";

        void RouteExternalIntegrationsEvent<TEvent>(string routingKey) where TEvent : IntegrationEvent =>
            opts.PublishMessage(typeof(TEvent))
                .ToRabbitRoutingKey(externalIntegrationsEventsExchange, routingKey, exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox()
                .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        RouteExternalIntegrationsEvent<WhatsAppMessageStatusChanged>("whatsapp_message_status_changed");

        // Fase 9, Checkpoint 3.2 ("Airbnb Deterministic Foundation"):
        // External Integrations' own Airbnb reservation events, published by
        // IAirbnbReservationSyncPublisher — same exchange as
        // WhatsAppMessageStatusChanged above (External Integrations' own
        // published-events exchange, never a new one per event type).
        // AirbnbSyncStarted is deliberately NOT routed here — it has no
        // consumer this checkpoint (mandate §8: deferred until a future sync
        // orchestration checkpoint), so publishing it now would only produce
        // an unconsumed, unbound message.
        RouteExternalIntegrationsEvent<AirbnbReservationImported>("airbnb_reservation_imported");
        RouteExternalIntegrationsEvent<AirbnbReservationUpdated>("airbnb_reservation_updated");
        RouteExternalIntegrationsEvent<AirbnbReservationCancelled>("airbnb_reservation_cancelled");

        // Fase 11, Checkpoint 1 (Inbound Conversation Foundation) — External
        // Integrations' second Integration Event, published by
        // WhatsAppWebhookMessageEventPublisher after signature verification,
        // tenant routing, and message normalization — same exchange as
        // WhatsAppMessageStatusChanged/Airbnb events above.
        RouteExternalIntegrationsEvent<InboundGuestMessageReceived>("inbound_guest_message_received");

        // Guest Operations' first Integration Event (Fase 10, Checkpoint 1 —
        // Guest Operations Foundation) — its own topic exchange, published by
        // RecordGuestCheckedOutCommandHandler. Workflow Orchestration's new
        // orchestrator (running in IHostPro.Worker) is its sole consumer —
        // see the Worker's own Program.cs for the corresponding
        // ListenToRabbitQueue.
        const string guestOperationsEventsExchange = "guest-operations-events";

        opts.PublishMessage(typeof(GuestCheckedOut))
            .ToRabbitRoutingKey(guestOperationsEventsExchange, "guest_checked_out", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Guest Operations' second Integration Event (Fase 10, Checkpoint 2 —
        // Check-in/Checkout Core), same exchange as GuestCheckedOut above
        // (a second routing key, never a second exchange), published by
        // RecordGuestCheckedInCommandHandler. Deliberately routed with no
        // current consumer (Front Desk is deferred to Checkpoint 4,
        // Communication deliberately adds no new consumer this checkpoint —
        // approved mandate) — an unbound topic message is simply dropped,
        // never an error, mirroring AirbnbSyncStarted's own precedent above.
        opts.PublishMessage(typeof(GuestCheckedIn))
            .ToRabbitRoutingKey(guestOperationsEventsExchange, "guest_checked_in", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Guest Operations' third and fourth Integration Events (Fase 10,
        // Checkpoint 3 — Early Check-in / Late Checkout), same exchange as
        // GuestCheckedOut/GuestCheckedIn above, published by
        // RequestEarlyCheckInCommandHandler. Workflow Orchestration's new
        // reschedule orchestrator (running in IHostPro.Worker) is its sole
        // consumer — see the Worker's own Program.cs for the corresponding
        // ListenToRabbitQueue.
        opts.PublishMessage(typeof(EarlyCheckinApproved))
            .ToRabbitRoutingKey(guestOperationsEventsExchange, "early_checkin_approved", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Deliberately routed with no current consumer — no Bounded Context
        // reacts to a denial this checkpoint (Reservation's schedule never
        // changes), mirroring GuestCheckedIn's own "no current consumer"
        // precedent above: an unbound topic message is simply dropped, never
        // an error.
        opts.PublishMessage(typeof(EarlyCheckinDenied))
            .ToRabbitRoutingKey(guestOperationsEventsExchange, "early_checkin_denied", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Published by RequestLateCheckoutCommandHandler — TWO consumers in
        // IHostPro.Worker (Workflow Orchestration's reschedule orchestrator,
        // ALWAYS; Housekeeping's own reaction, gated on UpdatesCleaning) —
        // see the Worker's own Program.cs for both corresponding
        // ListenToRabbitQueue calls (ADR-020 two independent subscriber
        // queues on this exchange).
        opts.PublishMessage(typeof(LateCheckoutApproved))
            .ToRabbitRoutingKey(guestOperationsEventsExchange, "late_checkout_approved", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Deliberately routed with no current consumer — mirrors
        // EarlyCheckinDenied above exactly.
        opts.PublishMessage(typeof(LateCheckoutDenied))
            .ToRabbitRoutingKey(guestOperationsEventsExchange, "late_checkout_denied", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Guest Operations' fifth Integration Event (Fase 10, Checkpoint 5 —
        // PIX/Payment Deterministic Foundation), same exchange as every
        // other Guest Operations event above, published by
        // RequestLateCheckoutCommandHandler when the resolved policy
        // requires PIX. Payments is the sole consumer — see the Worker's
        // own Program.cs for the corresponding ListenToRabbitQueue.
        opts.PublishMessage(typeof(LateCheckoutPaymentRequired))
            .ToRabbitRoutingKey(guestOperationsEventsExchange, "late_checkout_payment_required", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

        // Guest Operations' sixth Integration Event (Fase 10, Checkpoint 6.2
        // — Guest Access Secure Delivery Corrective Implementation), same
        // exchange as every other Guest Operations event above, published by
        // RequestGuestAccessDeliveryCommandHandler. Communication is the
        // sole consumer — see the Worker's own Program.cs for the
        // corresponding ListenToRabbitQueue.
        opts.PublishMessage(typeof(GuestAccessDeliveryRequested))
            .ToRabbitRoutingKey(guestOperationsEventsExchange, "guest_access_delivery_requested", exchange => exchange.ExchangeType = ExchangeType.Topic)
            .UseDurableOutbox()
            .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);
    });

    builder.Services.AddScoped<IEventPublisher, WolverineEventPublisher>();

    // OTLP endpoint is configured exclusively via appsettings/environment variables
    // (never hardcoded) — pipeline: App -> OTLP -> OpenTelemetry Collector -> Prometheus
    // -> Grafana (ADR-007). "OpenTelemetry__OtlpEndpoint" overrides it per environment.
    var otlpEndpoint = new Uri(builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317");

    // Fase 12, Checkpoint 2 (Observability Finalization) — three boundaries
    // that were structurally invisible to tracing before this checkpoint,
    // despite the OTel SDK already being registered: Wolverine's own
    // ActivitySource is a third-party library source, never captured unless
    // explicitly listened to (AddSource("Wolverine") — confirmed against
    // Wolverine's own documentation, verified missing beforehand); outbound
    // HTTP (AnthropicModelProvider's real REST calls, WhatsApp's real
    // outbound calls) had no instrumentation at all; Npgsql's own first-party
    // OTLP integration (AddNpgsql()) covers every DbContext's real SQL calls
    // uniformly, without touching any of the 11 per-context Infrastructure
    // projects individually. Never a custom span added to any application
    // method — only these three library-boundary instrumentations, per the
    // mandate's own "instrument boundaries, not every method" scope.
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName: "IHostPro.Api"))
        .WithTracing(tracing => tracing
            .AddSource("Wolverine")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddNpgsql()
            .AddOtlpExporter(o => o.Endpoint = otlpEndpoint))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddNpgsqlInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = otlpEndpoint));

    var app = builder.Build();

    // Fase 12, Checkpoint 4 — ordering matters and follows ASP.NET Core's
    // own documented guidance: ForwardedHeaders first (so every later
    // middleware/handler sees the real client scheme/IP once a real proxy is
    // configured, never before); the exception handler next, so it wraps
    // everything downstream; HSTS only when not Development (a dev
    // certificate is never publicly trusted, so HSTS there would just be
    // noise) and only ever added on top of HTTPS redirection, never instead
    // of it.
    app.UseForwardedHeaders(forwardedHeadersOptions);
    app.UseExceptionHandler();
    app.UseIHostProSecurityHeaders();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }
    else
    {
        app.UseHsts();
    }

    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
    app.UseCors(FrontendCorsPolicy);
    app.UseAuthentication();
    app.UseAuthorization();
    // Fase 12, Checkpoint 3 — after UseAuthorization so a partition keyed by
    // the authenticated TenantId/UserId claim (TenantApi/AdminApi policies)
    // sees a populated HttpContext.User; AllowAnonymous actions (Login/
    // Refresh) still reach UseRateLimiter fine — authentication middleware
    // runs regardless of [AllowAnonymous], it just never blocks the request.
    app.UseRateLimiter();
    // TenantApi is the default for every controller-routed endpoint;
    // [EnableRateLimiting("Authentication")] on Login/Refresh and
    // [EnableRateLimiting("AdminApi")] on the administrative controllers
    // each override this default for their own actions — see
    // RequireRateLimitingByDefault's own doc comment for why this is NOT a
    // plain .RequireRateLimiting(...) call (that silently wins over the
    // per-action attributes instead of yielding to them).
    app.MapControllers().RequireRateLimitingByDefault(ApiRateLimitingExtensions.TenantApiPolicy);
    // Fase 12, Checkpoint 2 — liveness (process is up, checks nothing) vs
    // readiness (checks every "ready"-tagged dependency) are now distinct,
    // matching Documento 21 §18's Healthy/Degraded/Unhealthy model instead
    // of a single endpoint that only ever said "the process didn't crash".
    // The response body is a minimal, explicitly-safe JSON shape (component
    // name + status + duration only) — never HealthReportEntry.Description/
    // Exception, which could otherwise surface a connection string or a raw
    // driver exception message.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = ObservabilityHealthCheckResponseWriter.WriteAsync,
    });
    // Preserved for backward compatibility with anything already polling
    // the original single endpoint — identical to /health/ready.
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = ObservabilityHealthCheckResponseWriter.WriteAsync,
    });

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "IHostPro.Api terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Exposed (global namespace, matching the implicit Program class generated for
// top-level statements) so integration tests can reference the entry point via
// WebApplicationFactory<Program> once those tests are written.
public partial class Program
{
    /// <summary>
    /// Reproduces Swashbuckle's default schemaId algorithm (generic-argument
    /// names concatenated as a prefix, then the type's own name), but adds a
    /// bounded-context prefix for generic types declared under
    /// IHostPro.Contexts.*, so that same-named generics declared
    /// independently in different contexts (e.g. Optional&lt;T&gt; in both
    /// PropertyManagement and Reservations) do not collide.
    /// </summary>
    internal static string SwaggerSchemaIdSelector(Type type)
    {
        if (!type.IsConstructedGenericType)
        {
            return type.Name.Replace("[]", "Array");
        }

        var prefix = type.GetGenericArguments()
            .Select(SwaggerSchemaIdSelector)
            .Aggregate((previous, current) => previous + current);

        var baseName = prefix + type.Name.Split('`')[0];

        var segments = type.Namespace?.Split('.');
        var contextName = segments is { Length: > 2 } && segments[0] == "IHostPro" && segments[1] == "Contexts"
            ? segments[2]
            : null;

        return contextName is null ? baseName : contextName + baseName;
    }

    /// <summary>
    /// Swashbuckle leaves <c>Operation.OperationId</c> unset by default in
    /// this project (no <see cref="Microsoft.AspNetCore.Mvc.SwaggerGenOptions.CustomOperationIds"/>
    /// configured until this gate) — the generated <c>swagger.json</c> never
    /// contained an <c>operationId</c> field at all. NSwag's TypeScript
    /// generator then synthesizes its own operation name from the LAST route
    /// segment when generating the client, entirely independent of the C#
    /// action method name: both <c>ReservationsController.CancelReservation</c>
    /// and <c>CleaningsController.CancelCleaning</c> end in
    /// <c>".../cancel"</c>, so NSwag produced two identical synthetic names
    /// ("cancel"/"cancel2") — a genuine regression that silently pointed
    /// Reservations' already-shipped "cancel" client method at the Cleanings
    /// route instead (Fase 6 homologation document, Checkpoint 5).
    ///
    /// This resolver assigns an explicit, unique, semantic OperationId to
    /// exactly the two actions known to collide under NSwag's path-based
    /// fallback naming — every other action returns <see langword="null"/>,
    /// preserving today's behavior unchanged (Swashbuckle omits OperationId
    /// entirely when the selector returns null, exactly as it does today for
    /// every action). A global <c>{Controller}_{Action}</c> convention was
    /// deliberately rejected: <c>nswag.json</c> already sets
    /// <c>"className": "{controller}Client"</c> with
    /// <c>"operationGenerationMode": "MultipleClientsFromOperationId"</c>,
    /// which only takes effect when OperationId contains an underscore —
    /// assigning that format to every action would retroactively split the
    /// single shared generated <c>Client</c> class into one class per
    /// controller, breaking every existing frontend service's
    /// <c>inject(Client)</c> call. This targeted resolver avoids that
    /// entirely: neither "CancelReservation" nor "CancelCleaning" contains an
    /// underscore, so both land on the same shared <c>Client</c> class as
    /// every other operation, just with collision-proof, human-readable
    /// method names (<c>cancelReservation()</c>/<c>cancelCleaning()</c>).
    /// Any future reintroduction of this class of collision (two actions in
    /// different controllers ending in the same route segment) is caught by
    /// <c>OpenApiOperationIdTests</c> (<c>IHostPro.Api.Tests.Integration</c>),
    /// which asserts against the real, fully-composed OpenAPI document that
    /// no two operations ever share an OperationId — not by this resolver
    /// growing new special cases proactively.
    /// </summary>
    internal static string? SwaggerOperationIdSelector(Microsoft.AspNetCore.Mvc.ApiExplorer.ApiDescription apiDescription)
    {
        if (apiDescription.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor descriptor)
        {
            if (descriptor is { ControllerName: "Reservations", ActionName: "CancelReservation" })
                return "CancelReservation";
            if (descriptor is { ControllerName: "Cleanings", ActionName: "CancelCleaning" })
                return "CancelCleaning";

            // Fase 6, Incremento 2A — MyCleaningsController's self-service
            // lifecycle actions share the exact same LAST route segment
            // (".../start", ".../start-inspection", ".../complete",
            // ".../waiting-materials", ".../waiting-help") as their
            // administrative CleaningsController counterparts, the same
            // class of collision as CancelReservation/CancelCleaning above.
            // Only the self-service side needs an explicit id — leaving
            // CleaningsController's own actions untouched (still null here)
            // preserves the already-shipped admin frontend's
            // start()/startInspection()/complete()/waitingMaterials()/waitingHelp()
            // method names exactly as they are today.
            if (descriptor.ControllerName == "MyCleanings")
            {
                return descriptor.ActionName switch
                {
                    "Start" => "StartOwnCleaning",
                    "StartInspection" => "StartOwnCleaningInspection",
                    "Complete" => "CompleteOwnCleaning",
                    "WaitingMaterials" => "MarkOwnCleaningWaitingMaterials",
                    "WaitingHelp" => "MarkOwnCleaningWaitingHelp",
                    _ => null,
                };
            }
        }

        return null;
    }
}
