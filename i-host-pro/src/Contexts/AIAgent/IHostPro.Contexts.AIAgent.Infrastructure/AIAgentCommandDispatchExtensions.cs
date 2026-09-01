using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.AIAgent.Infrastructure;

/// <summary>
/// Api-only composition entry point for AI Agent's write-Command surface
/// (Fase 11, Checkpoint 6) — mirrors the original, pre-Checkpoint-4
/// <c>GuestOperationsCommandDispatchExtensions</c> shape (before that
/// context's own write Tools forced its Command surface into the shared
/// Worker module too). <see cref="ResumeAgentSessionCommand"/>'s only real
/// consumer is <c>IHostPro.Api</c>'s Resume-session endpoint —
/// <c>IHostPro.Worker</c> never calls it, so this method is never called
/// from there, and <see cref="AIAgentApplicationMediatorExtensions.AddAIAgentApplicationMediator"/>
/// stays out of <see cref="AIAgentModuleExtensions.AddAIAgentModule"/>
/// entirely.
///
/// Deliberately does NOT call <see cref="AIAgentModuleExtensions.AddAIAgentModule"/> —
/// that method also registers every Read/Write Tool, each needing another
/// Bounded Context's own request dispatcher (Reservations/PropertyManagement/
/// Housekeeping/Payments/GuestOperations) that <c>IHostPro.Api</c> does not
/// uniformly compose (Payments and Communication have no Api-hosted module
/// today). This method registers only what <see cref="ResumeAgentSessionCommandHandler"/>
/// actually needs: the AIAgentDbContext, the two repositories, and the
/// transaction executor — never a Tool, never <c>IModelProvider</c>.
/// </summary>
public static class AIAgentCommandDispatchExtensions
{
    public static IServiceCollection AddAIAgentCommandDispatch(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AIAgentDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("AIAgent"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "ai_agent")));

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IAgentSessionRepository, AgentSessionRepository>();
        services.AddScoped<IAgentHumanHandoffRepository, AgentHumanHandoffRepository>();
        services.AddScoped<IAIAgentTransactionExecutor, AIAgentTransactionExecutor>();

        services.AddAIAgentApplicationMediator();

        return services;
    }
}
