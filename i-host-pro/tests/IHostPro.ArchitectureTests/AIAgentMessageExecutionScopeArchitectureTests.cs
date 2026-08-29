using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.Messaging;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces ADR-016 (seventh application, Fase 11, Checkpoint 2 — AI Agent
/// Foundation), generalizing Communication's/Housekeeping's/Reservations'/
/// Dashboard's/Guest Operations'/Payments' own findings to AI Agent.
/// <c>AIAgentMessageExecutionScope</c> is the single, deliberately-authorized
/// boundary holding <see cref="IServiceScopeFactory"/> — the single thin
/// Wolverine adapter (<c>ConversationMessageReceivedHandler</c>) must keep
/// depending only on the ordinary constructor-injected graph, never
/// resolving <c>AIAgentDbContext</c>/<c>IAIAgentTransactionExecutor</c>/the
/// processor directly. Mirrors <c>CommunicationMessageExecutionScopeArchitectureTests</c>
/// exactly.
/// </summary>
public class AIAgentMessageExecutionScopeArchitectureTests
{
    private static readonly Type[] AIAgentAssemblyAnchors =
    [
        typeof(AgentSession),
        typeof(IAIAgentMessageExecutionScope),
        typeof(AIAgentMessageExecutionScope),
    ];

    [Fact]
    public void Only_AIAgentMessageExecutionScope_May_Depend_On_IServiceScopeFactory()
    {
        var typesDependingOnScopeFactory = AIAgentAssemblyAnchors
            .Select(anchor => anchor.Assembly)
            .Distinct()
            .SelectMany(assembly => Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn("Microsoft.Extensions.DependencyInjection.IServiceScopeFactory")
                .GetTypes())
            .Distinct()
            .ToList();

        typesDependingOnScopeFactory.Should().ContainSingle()
            .Which.Should().Be(typeof(AIAgentMessageExecutionScope),
                "IAIAgentMessageExecutionScope's own implementation is the single, deliberately-authorized " +
                "holder of IServiceScopeFactory in AI Agent (ADR-016) — any other match means a new class " +
                "started resolving its own child scope outside the approved boundary.");
    }

    [Fact]
    public void Wolverine_Adapter_Never_Depends_On_AIAgentDbContext_Or_TransactionExecutor_Or_Processor_Or_ServiceScopeFactory()
    {
        var adapterTypes = Types.InAssembly(typeof(AIAgentMessageExecutionScope).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.AIAgent.Infrastructure.Messaging")
            .And()
            .DoNotHaveName(nameof(AIAgentMessageExecutionScope))
            .GetTypes();

        adapterTypes.Should().ContainSingle(t => t.Name == nameof(ConversationMessageReceivedHandler));

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.AIAgent.Infrastructure.Persistence.AIAgentDbContext",
            "IHostPro.Contexts.AIAgent.Application.IAIAgentTransactionExecutor",
            "IHostPro.Contexts.AIAgent.Application.ConversationMessageReceivedProcessor",
            "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
            "Wolverine.EntityFrameworkCore.IDbContextOutbox",
        };

        var result = Types.InAssembly(typeof(AIAgentMessageExecutionScope).Assembly)
            .That()
            .HaveName(nameof(ConversationMessageReceivedHandler))
            .Should()
            .NotHaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "ConversationMessageReceivedHandler must depend only on IAIAgentMessageExecutionScope for anything " +
            "AI-Agent-persistence-related (ADR-016)");
    }
}
