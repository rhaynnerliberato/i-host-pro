using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Identity.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Wolverine;

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

    // IHostPro.Worker hosts every Bounded Context's message handlers and Sagas,
    // kept in a separate process from IHostPro.Api so message processing can
    // scale independently of HTTP traffic (Architecture Principles, Section 2).
    // Handlers are plain classes discovered by Wolverine's naming convention —
    // no Bounded Context ever implements a Wolverine-specific interface
    // (Architecture Principles, Section 11; ADR-004).
    builder.UseWolverine(opts =>
    {
        opts.UseIHostProRabbitMq(builder.Configuration, listen: true);

        opts.Policies.AddMiddleware(
            typeof(TenantResolutionMiddleware),
            chain => typeof(IntegrationEvent).IsAssignableFrom(chain.MessageType));
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
