using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Messaging;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
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
        services.AddScoped<IAgentToolExecutionRepository, AgentToolExecutionRepository>();
        services.AddScoped<IAgentPendingActionRepository, AgentPendingActionRepository>();
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

        // Fase 11, Checkpoint 3 — the exact, closed set of 8 approved Read
        // Tools (AgentToolNames) — each calls its owning Bounded Context's
        // own Application Query via that context's I<Context>RequestDispatcher
        // (Exception #3). The dispatchers themselves are already registered
        // by AddReservationsModule/AddPropertyManagementModule/
        // AddHousekeepingModule/AddConfigurationModule/AddPaymentsModule,
        // all of which IHostPro.Worker already calls before this method.
        services.AddScoped<IAgentTool, GetReservationSummaryTool>();
        services.AddScoped<IAgentTool, GetScheduleTool>();
        services.AddScoped<IAgentTool, GetAvailabilityTool>();
        services.AddScoped<IAgentTool, GetPropertyInformationTool>();
        services.AddScoped<IAgentTool, GetAccessInstructionsTool>();
        services.AddScoped<IAgentTool, GetCleaningStatusTool>();
        services.AddScoped<IAgentTool, GetPaymentStatusTool>();
        services.AddScoped<IAgentTool, GetRelevantPoliciesTool>();

        // Fase 11, Checkpoint 4 — the exact, closed set of 3 approved
        // business Write Tools. RequestEarlyCheckInTool/RequestLateCheckoutTool
        // also implement IConfirmableAgentTool (CONFIRMATION_REQUIRED);
        // RequestGuestAccessDeliveryTool is a plain IAgentTool
        // (EXPLICIT_REQUEST_IS_CONFIRMATION). All three call Guest
        // Operations' own Application Commands via
        // IGuestOperationsRequestDispatcher (Exception #3) — already
        // registered by AddGuestOperationsModule, which IHostPro.Worker
        // calls before this method.
        services.AddScoped<IAgentTool, RequestEarlyCheckInTool>();
        services.AddScoped<IAgentTool, RequestLateCheckoutTool>();
        services.AddScoped<IAgentTool, RequestGuestAccessDeliveryTool>();

        services.AddScoped<IAgentToolConfirmationPolicy, AgentToolConfirmationPolicy>();

        // Fase 11, Checkpoint 4 — SendAgentResponseCommand's own Exception #3
        // adapter (never a model-callable Tool). ICommunicationRequestDispatcher
        // is already registered by AddCommunicationModule, which IHostPro.Worker
        // calls before this method.
        services.AddScoped<IAgentResponseDeliveryService, AgentResponseDeliveryService>();

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
