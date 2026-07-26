using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
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
    // middleware once Identity & Access is implemented (Phase 1). The scoped
    // instance is registered here so every downstream service in the request
    // can depend on ITenantContext (Architecture Principles, Section 7).
    builder.Services.AddScoped<ITenantContext, TenantContext>();

    builder.Services.AddHealthChecks();

    // IHostPro.Api only publishes Integration Events (via IEventPublisher); it never
    // consumes messages — consumers/handlers live exclusively in IHostPro.Worker
    // (Architecture Principles, Section 2). "listen: false" means this process
    // never creates receive queues (sender-only connection).
    builder.Host.UseWolverine(opts =>
    {
        opts.UseIHostProRabbitMq(builder.Configuration, listen: false);
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

    // NOTE (Phase 0 scope): Bounded Context modules (Identity, Reservations, etc.)
    // do not exist yet. Each module will be registered here through a single
    // extension method (e.g. `builder.Services.AddReservationsModule(...)`) as it
    // is implemented in its corresponding phase, per Architecture Principles §16.

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
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
