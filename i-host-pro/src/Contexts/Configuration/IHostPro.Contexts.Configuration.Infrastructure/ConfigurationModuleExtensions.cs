using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Infrastructure.Caching;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Infrastructure.Resolution;
using IHostPro.Contexts.Configuration.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Configuration.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Configuration &amp; Policy
/// module — mirrors <c>ReservationsModuleExtensions</c> exactly. The Host
/// (IHostPro.Api) calls this once.
/// </summary>
public static class ConfigurationModuleExtensions
{
    private const string UncachedResolverKey = "uncached";

    public static IServiceCollection AddConfigurationModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — mirrors
        // ReservationsModuleExtensions/PropertyManagementModuleExtensions.
        services.AddDbContext<ConfigurationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Configuration"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration")));

        services.AddSingleton(TimeProvider.System);

        // The generic resolver stays internal to this assembly (Fase 5,
        // Incremento 1 official decisions §5) — only the two policy-specific
        // readers below are registered against their public Contracts
        // interfaces. IPolicyValueResolver resolves to the caching decorator
        // (Checkpoint 6); the real, DB-only PolicyValueResolver is registered
        // under the same IPolicyValueResolver interface but keyed, so
        // CachedPolicyValueResolver can depend on the interface (unit-testable
        // with a fake) rather than the concrete type, with no DI cycle.
        services.AddKeyedScoped<IPolicyValueResolver, PolicyValueResolver>(UncachedResolverKey);
        services.AddScoped<IPolicyValueResolver>(sp => new CachedPolicyValueResolver(
            sp.GetRequiredKeyedService<IPolicyValueResolver>(UncachedResolverKey),
            sp.GetRequiredService<IPolicyValueCache>()));
        services.AddScoped<IEarlyCheckInPolicyReader, EarlyCheckInPolicyReader>();
        services.AddScoped<ILateCheckoutPolicyReader, LateCheckoutPolicyReader>();
        // Fase 11, Checkpoint 7 — AI Agent's own Context Builder consumes this directly (Exceção 1).
        services.AddScoped<IAiAgentBehaviorPolicyReader, AiAgentBehaviorPolicyReader>();

        // Fase 9, Checkpoint 1 — "Comunicação e Integrações do MVP": the
        // general Configuration & Policy synchronous-query exception
        // (Architecture Principles §14, Exceção 1) extended to Templates.
        // Registered here (not only alongside the CRUD pipeline in
        // AddConfigurationCommandDispatch) because Communication's own
        // Wolverine consumer runs in IHostPro.Worker, and AddConfigurationModule
        // already runs in both processes.
        services.AddScoped<ITemplateReader, TemplateReader>();

        services.AddConfigurationPolicyCache(configuration);

        // Fase 11, Checkpoint 3 — Exception #3 (AI Agent Tools -> Application
        // Services): the AI Agent's own Wolverine consumer runs in
        // IHostPro.Worker and needs to execute GetEffectivePolicyQuery
        // in-process via IConfigurationRequestDispatcher. Query-only
        // Application Mediator wiring is promoted here (shared Module) so
        // both processes get it — GetEffectivePolicyQueryHandler needs
        // nothing beyond IEarlyCheckInPolicyReader/ILateCheckoutPolicyReader,
        // already registered above, and deliberately gets no
        // TenantTransactionBehavior (see GetEffectivePolicyQuery's own doc
        // comment). Write Commands/validators/write pipeline behaviors
        // remain Api-only — see ConfigurationCommandDispatchExtensions'
        // own updated doc comment.
        //
        // This shared method must never trim Mediator's handler
        // registration (see ReservationsModuleExtensions' own identical
        // comment) — IHostPro.Api's real write HTTP endpoints depend on
        // every handler staying registered here. IHostPro.Worker's own
        // Program.cs calls KeepOnlyMediatorHandlers right after this method
        // returns, to trim its OWN composition down to the one approved
        // read-only query handler (a real Worker ValidateOnBuild startup
        // crash found and fixed during CP3 homologation).
        services.AddConfigurationApplicationMediator();

        return services;
    }
}
