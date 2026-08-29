using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Infrastructure.Messaging;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.AIAgent.Infrastructure;

/// <summary>
/// Single composition-root entry point for the AI Agent module (Fase 11,
/// Checkpoint 2) — mirrors <c>CommunicationModuleExtensions</c> exactly.
/// Consumed exclusively in <c>IHostPro.Worker</c> — <c>IHostPro.Api</c> never
/// references it (no HTTP surface, mandate item 31/41).
/// </summary>
public static class AIAgentModuleExtensions
{
    public static IServiceCollection AddAIAgentModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AIAgentDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("AIAgent"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "ai_agent")));

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IAgentSessionRepository, AgentSessionRepository>();
        services.AddScoped<IAgentInteractionRepository, AgentInteractionRepository>();
        services.AddScoped<IAIAgentTransactionExecutor, AIAgentTransactionExecutor>();
        services.AddScoped<IAgentSessionResolver, AgentSessionResolver>();
        services.AddScoped<IAgentContextBuilder, AgentContextBuilder>();

        // Fase 11, Checkpoint 2's ONLY implementation of IModelProvider — a
        // deterministic fake, never a real Anthropic client (real Anthropic
        // integration is Checkpoint 7's scope, mandate item 17/25).
        services.AddScoped<IModelProvider, FakeModelProvider>();

        // ADR-016 — the single, deliberately-authorized holder of
        // IServiceScopeFactory in AI Agent (mirrors CommunicationMessageExecutionScope).
        services.AddScoped<IAIAgentMessageExecutionScope, AIAgentMessageExecutionScope>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="ConversationMessageReceivedProcessor"/> — the
    /// real session-creation flow's own Wolverine consumer (mandate item 30).
    /// Unconditional in every environment, same rationale as Communication's
    /// own inbound message consumer: resolving a session and calling the
    /// (deterministic, zero-network) FakeModelProvider has no fake/real
    /// distinction of its own beyond the provider itself. Keyed by
    /// <see cref="AIAgentMessageExecutionScope.HandlerKey"/> — mirrors
    /// Communication's own keyed-handler discipline exactly (ADR-016).
    /// </summary>
    public static IServiceCollection AddAIAgentConversationMessageConsumer(this IServiceCollection services)
    {
        services.AddKeyedScoped<IIntegrationEventHandler<ConversationMessageReceived>, ConversationMessageReceivedProcessor>(
            AIAgentMessageExecutionScope.HandlerKey);

        return services;
    }
}
