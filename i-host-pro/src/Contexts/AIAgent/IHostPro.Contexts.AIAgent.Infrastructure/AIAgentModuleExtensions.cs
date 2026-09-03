using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Context;
using IHostPro.Contexts.AIAgent.Infrastructure.Messaging;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using Polly;
using Polly.CircuitBreaker;
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
    /// <param name="isDevelopmentEnvironment">
    /// Whether the calling host is running in the Development environment
    /// (Fase 11, Checkpoint 7) — passed explicitly, never resolved via
    /// <c>IHostEnvironment</c> inside this method, mirroring
    /// <c>AddExternalIntegrationsModule</c>'s own precedent exactly. Gates
    /// which <see cref="IAnthropicCredentialProvider"/> is registered:
    /// <see cref="DevelopmentAnthropicCredentialProvider"/> (User Secrets/
    /// environment variables) in Development, <see cref="SecretsManagerAnthropicCredentialProvider"/>
    /// (AWS Secrets Manager, Fase 12 CP5.3A) everywhere else — never a
    /// silent fallback between the two.
    /// </param>
    public static IServiceCollection AddAIAgentModule(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopmentEnvironment)
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
        services.AddScoped<IAgentHumanHandoffRepository, AgentHumanHandoffRepository>();
        services.AddScoped<IAIAgentTransactionExecutor, AIAgentTransactionExecutor>();
        services.AddScoped<IAgentSessionResolver, AgentSessionResolver>();
        services.AddScoped<IAgentContextBuilder, AgentContextBuilder>();

        // Fase 12, Checkpoint 3 — conversation-history token budget (closes
        // ProductionContextBudgetStrategyRequired from Fase 11 CP7). Mirrors
        // AnthropicOptions' own Configure<T> shape exactly — no
        // ValidateOnStart needed, every field has a safe conservative
        // default and no external connection to validate.
        services.Configure<ContextBudgetOptions>(configuration.GetSection(ContextBudgetOptions.SectionName));
        services.AddSingleton<IContextBudgetPolicy, IHostPro.Contexts.AIAgent.Infrastructure.ContextBudget.ContextBudgetPolicy>();

        // Fase 12, Checkpoint 3 (Resilience & Rate Limiting) — the
        // "AiExpensiveOperation" cost-guard, applied by
        // ConversationMessageReceivedProcessor at the real orchestration
        // boundary (never an HTTP endpoint — the AI Agent has none).
        // Delegates to the shared IDistributedRateLimiter, registered by
        // IHostPro.Worker's own AddIHostProRateLimiting call.
        services.AddSingleton<IAiAgentRateLimiter, IHostPro.Contexts.AIAgent.Infrastructure.RateLimiting.AiAgentRateLimiter>();

        // Fase 11, Checkpoint 7 — reuses the same Reservations/PropertyManagement
        // dispatchers GetPropertyInformationTool already depends on.
        services.AddScoped<IPropertyLocalTimeContextReader, PropertyLocalTimeContextReader>();

        // Fase 11, Checkpoint 7 — explicit provider selection (mandate item
        // 9/29/45): AIAgent:ModelProvider = "Fake" (default — deterministic,
        // zero network, every automated test suite) or "Anthropic" (real
        // REST client, ADR-009). An unrecognized, explicitly-set value fails
        // loudly at startup rather than silently defaulting to Fake — never
        // let a typo quietly downgrade a real deployment to Fake responses.
        var modelProviderName = configuration["AIAgent:ModelProvider"];
        if (string.IsNullOrWhiteSpace(modelProviderName) || string.Equals(modelProviderName, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IModelProvider, FakeModelProvider>();
        }
        else if (string.Equals(modelProviderName, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<AnthropicOptions>(configuration.GetSection("AIAgent:Anthropic"));

            // Fase 12, Checkpoint 3 — circuit breaker options bound directly
            // from IConfiguration here (never via DI/IOptions inside the
            // resilience pipeline builder below, which runs at pipeline-build
            // time, not per-request) — this checkpoint has no hot-reload
            // requirement for these values.
            var circuitBreakerOptions = configuration.GetSection("AIAgent:Anthropic:CircuitBreaker").Get<HttpCircuitBreakerOptions>()
                ?? new HttpCircuitBreakerOptions();

            var anthropicHttpClientBuilder = services.AddHttpClient(AnthropicModelProvider.HttpClientName, (serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnthropicOptions>>().Value;
                var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });

            // Fase 12, Checkpoint 3, Decision Gate amendment — official
            // Microsoft.Extensions.Http.Resilience circuit breaker ONLY
            // (AddCircuitBreaker is the one stage added; no AddRetry/
            // AddHedging/AddTimeout — never a second, competing retry
            // mechanism alongside ConversationMessageReceivedProcessor's own
            // already-homologated single application-level retry). Never
            // opens for a permanent failure (400/401/403/404 — the exact
            // same IsPermanentFailure classification AnthropicModelProvider
            // itself already uses, reused here rather than duplicated) —
            // only for network errors, timeouts, 429, and 5xx.
            if (circuitBreakerOptions.Enabled)
            {
                anthropicHttpClientBuilder.AddResilienceHandler("anthropic-circuit-breaker", builder =>
                {
                    builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                    {
                        FailureRatio = circuitBreakerOptions.FailureRatio,
                        MinimumThroughput = circuitBreakerOptions.MinimumThroughput,
                        SamplingDuration = circuitBreakerOptions.SamplingDuration,
                        BreakDuration = circuitBreakerOptions.BreakDuration,
                        ShouldHandle = args => ValueTask.FromResult(
                            args.Outcome.Exception is HttpRequestException or TaskCanceledException ||
                            (args.Outcome.Result is { } response &&
                                (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))),
                        OnOpened = _ =>
                        {
                            IHostPro.BuildingBlocks.Infrastructure.Resilience.CircuitBreakerTelemetry.RecordStateChange("Anthropic", "Opened");
                            return ValueTask.CompletedTask;
                        },
                        OnClosed = _ =>
                        {
                            IHostPro.BuildingBlocks.Infrastructure.Resilience.CircuitBreakerTelemetry.RecordStateChange("Anthropic", "Closed");
                            return ValueTask.CompletedTask;
                        },
                        OnHalfOpened = _ =>
                        {
                            IHostPro.BuildingBlocks.Infrastructure.Resilience.CircuitBreakerTelemetry.RecordStateChange("Anthropic", "HalfOpened");
                            return ValueTask.CompletedTask;
                        },
                    });
                });
            }

            services.AddScoped<IModelProvider, AnthropicModelProvider>();

            // Fase 12, CP5.3A: outside Development, IAnthropicCredentialProvider
            // is now backed by AWS Secrets Manager instead of being left
            // unregistered - HomologRealAnthropicRequired=true. AmazonSecretsManagerClient
            // uses the default AWS credential chain (the ECS task role in
            // Homolog/Production), never a key configured here.
            if (isDevelopmentEnvironment)
                services.AddScoped<IAnthropicCredentialProvider, DevelopmentAnthropicCredentialProvider>();
            else
            {
                services.AddSingleton<Amazon.SecretsManager.IAmazonSecretsManager>(
                    new Amazon.SecretsManager.AmazonSecretsManagerClient());
                services.AddSingleton<ISecretValueReader, AwsSecretsManagerValueReader>();
                services.AddScoped<IAnthropicCredentialProvider, SecretsManagerAnthropicCredentialProvider>();
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown AIAgent:ModelProvider '{modelProviderName}'. Valid values: 'Fake', 'Anthropic'.");
        }

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

        // Fase 11, Checkpoint 6 (Human Handoff, Safety & Audit) — the safety
        // classifier (fixed Intent -> AgentHumanHandoffReasonCode allowlist,
        // never model-supplied) and SendHumanHandoffNotificationCommand's own
        // Exception #3 adapter, mirroring IAgentResponseDeliveryService's
        // shape exactly.
        services.AddScoped<IAgentHumanHandoffReasonClassifier, AgentHumanHandoffReasonClassifier>();
        services.AddScoped<IAdministratorNotificationService, AdministratorNotificationService>();

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
