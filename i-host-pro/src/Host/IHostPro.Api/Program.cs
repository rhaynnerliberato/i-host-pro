using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using JasperFx;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

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

    builder.Services.AddHealthChecks();

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

    // IHostPro.Api only publishes Integration Events (via IEventPublisher); it never
    // consumes messages — consumers/handlers live exclusively in IHostPro.Worker
    // (Architecture Principles, Section 2). "listen: false" means this process
    // never creates receive queues (sender-only connection).
    builder.Host.UseWolverine(opts =>
    {
        opts.UseIHostProRabbitMq(builder.Configuration, listen: false);

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
    });

    builder.Services.AddScoped<IEventPublisher, WolverineEventPublisher>();

    // OTLP endpoint is configured exclusively via appsettings/environment variables
    // (never hardcoded) — pipeline: App -> OTLP -> OpenTelemetry Collector -> Prometheus
    // -> Grafana (ADR-007). "OpenTelemetry__OtlpEndpoint" overrides it per environment.
    var otlpEndpoint = new Uri(builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName: "IHostPro.Api"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = otlpEndpoint))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = otlpEndpoint));

    // Future Bounded Context modules (Reservations, etc.) are registered here
    // through their own single extension method
    // (e.g. `builder.Services.AddReservationsModule(...)`) as each is
    // implemented in its corresponding phase, per Architecture Principles §16.

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHealthChecks("/health");

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
}
