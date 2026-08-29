using System.Runtime.CompilerServices;
using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.Communication.Contracts;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — mandate item 44. Replaces
/// the two now-retired CP1-only guards in
/// <c>InboundConversationFoundationArchitectureTests</c> with the positive
/// assertions CP2 itself requires. Architecture Principles §14 now has 14
/// named synchronous exceptions (ADR-030, Exception #14).
/// </summary>
public class AIAgentFoundationArchitectureTests
{
    private static string RepositoryRoot([CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", ".."));

    [Fact]
    public void Domain_Never_Depends_On_Application_Infrastructure_Or_Any_Other_Bounded_Context()
    {
        var result = Types.InAssembly(typeof(AgentSession).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.AIAgent.Application",
                "IHostPro.Contexts.AIAgent.Infrastructure",
                "IHostPro.Contexts.Communication",
                "IHostPro.Contexts.Reservations",
                "IHostPro.Contexts.GuestOperations",
                "IHostPro.Contexts.Payments",
                "IHostPro.Contexts.PropertyManagement",
                "IHostPro.Contexts.Identity",
                "IHostPro.Contexts.Dashboard",
                "IHostPro.Contexts.Workflow",
                "IHostPro.Contexts.Housekeeping",
                "IHostPro.Contexts.Configuration",
                "IHostPro.Contexts.ExternalIntegrations",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// Mandate item 44: AIAgent.Application may reference other Bounded
    /// Contexts EXCLUSIVELY through Communication.Contracts (the trigger
    /// event, <c>ConversationMessageReceived</c>) — never any Domain/
    /// Application/Infrastructure/Api layer of any other context.
    /// </summary>
    [Fact]
    public void Application_Only_References_Communication_Contracts_Among_Other_Bounded_Contexts()
    {
        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.Communication.Domain",
            "IHostPro.Contexts.Communication.Application",
            "IHostPro.Contexts.Communication.Infrastructure",
            "IHostPro.Contexts.Communication.Api",
            "IHostPro.Contexts.Reservations",
            "IHostPro.Contexts.GuestOperations",
            "IHostPro.Contexts.Payments",
            "IHostPro.Contexts.PropertyManagement",
            "IHostPro.Contexts.Identity",
            "IHostPro.Contexts.Dashboard",
            "IHostPro.Contexts.Workflow",
            "IHostPro.Contexts.Housekeeping",
            "IHostPro.Contexts.Configuration",
            "IHostPro.Contexts.ExternalIntegrations",
        };

        var result = Types.InAssembly(typeof(IModelProvider).Assembly)
            .Should()
            .NotHaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>Mandate item 44: no arbitrary/generic Tool executor exists — Tool surface is allowlisted only, ZERO business Tools this checkpoint (mandate item 21/22).</summary>
    [Fact]
    public void No_Generic_Or_Arbitrary_Tool_Executor_Type_Exists_Anywhere_In_AIAgent()
    {
        var aiAgentAssemblies = new[] { typeof(AgentSession).Assembly, typeof(IModelProvider).Assembly };
        var forbiddenTypeNames = new[]
        {
            "ExecuteCommandTool", "GenericApiTool", "HttpTool", "SqlTool", "ArbitraryFunctionTool",
            "ReservationTool", "PaymentTool", "EarlyCheckinTool",
        };

        foreach (var assembly in aiAgentAssemblies)
        {
            var typeNames = assembly.GetTypes().Select(t => t.Name).ToList();
            foreach (var forbidden in forbiddenTypeNames)
            {
                typeNames.Should().NotContain(forbidden,
                    $"{forbidden} is a business/generic Tool — CP2 implements ZERO business Tools (mandate item 21/22)");
            }
        }
    }

    /// <summary>Mandate item 44: no Anthropic (or any other real provider) DTO/type leaks into AIAgent.Domain/Application — the sole IModelProvider implementation this checkpoint is FakeModelProvider.</summary>
    [Fact]
    public void No_Anthropic_Or_Real_Provider_Type_Exists_In_Domain_Or_Application()
    {
        var aiAgentAssemblies = new[] { typeof(AgentSession).Assembly, typeof(IModelProvider).Assembly };
        var forbiddenSubstrings = new[] { "Anthropic", "Claude" };

        foreach (var assembly in aiAgentAssemblies)
        {
            var typeNames = assembly.GetTypes().Select(t => t.Name).ToList();
            foreach (var forbidden in forbiddenSubstrings)
            {
                typeNames.Should().NotContain(
                    name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{assembly.GetName().Name} must never declare a type referencing '{forbidden}' — real Anthropic integration is Checkpoint 7's scope");
            }
        }
    }

    /// <summary>Mandate item 44/12: no AgentSession/AgentInteraction/ModelRequest/ModelResult property ever carries a secret, QR payload, or access credential.</summary>
    [Theory]
    [InlineData(typeof(AgentSession))]
    [InlineData(typeof(AgentInteraction))]
    [InlineData(typeof(ModelRequest))]
    [InlineData(typeof(ModelResult))]
    public void AIAgent_Types_Never_Carry_A_Secret_QrCode_Or_AccessCredential_Property(Type type)
    {
        var propertyNames = type.GetProperties().Select(p => p.Name).ToList();

        // "Token" is deliberately excluded — InputTokens/OutputTokens (LLM
        // usage counts, mandate item 15/29) are legitimate required fields,
        // unrelated to an authentication/API token.
        foreach (var forbidden in new[] { "Secret", "QrCode", "AccessCredential", "AccessKey", "ApiKey" })
        {
            propertyNames.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{type.Name} must never carry a property containing '{forbidden}'");
        }
    }

    /// <summary>Mandate item 41: default CP2 has ZERO AIAgent public API — no <c>IHostPro.Contexts.AIAgent.Api</c> project exists.</summary>
    [Fact]
    public void No_AIAgent_Api_Project_Exists()
    {
        var apiProjectPath = Path.Combine(RepositoryRoot(), "src", "Contexts", "AIAgent", "IHostPro.Contexts.AIAgent.Api");

        Directory.Exists(apiProjectPath).Should().BeFalse(
            "AI Agent has zero public API by default this checkpoint (mandate item 41) — " +
            "processing is event-driven; creating an Api project requires explicit new authorization");
    }

    /// <summary>
    /// ADR-030's own testable consequence (Fase 11, Checkpoint 2, synchronous
    /// exception #14): AI Agent is the ONLY Bounded Context authorized to
    /// consume <c>IConversationHistoryReader</c> — Communication owns/
    /// implements it, everyone else must never reference it.
    /// </summary>
    [Fact]
    public void No_Other_Context_Assembly_References_IConversationHistoryReader_Except_AIAgent()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.GuestOperations.Domain.GuestStayOperation).Assembly,
            typeof(IHostPro.Contexts.Payments.Domain.PixCharge).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.ExternalIntegrationsDbContext).Assembly,
        };

        var readerFullName = typeof(IConversationHistoryReader).FullName!;

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var referencingTypes = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(readerFullName)
                .GetTypes();

            referencingTypes.Should().BeEmpty(
                "only Communication (owner) and AI Agent (the sole authorized consumer, ADR-030 exception #14) " +
                $"may reference IConversationHistoryReader — {assembly.GetName().Name} referencing it would mean " +
                "an unauthorized Bounded Context bypassed the purpose-limited exception");
        }
    }

    /// <summary>No other Bounded Context may ever reference AIAgent's internal layers — only its own Contracts (once it publishes anything).</summary>
    [Fact]
    public void No_Other_Bounded_Context_Ever_References_AIAgent_Domain_Application_Or_Infrastructure()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Communication.Domain.Message).Assembly,
            typeof(IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContext).Assembly,
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.GuestOperations.Domain.GuestStayOperation).Assembly,
            typeof(IHostPro.Contexts.Payments.Domain.PixCharge).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.ExternalIntegrationsDbContext).Assembly,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "IHostPro.Contexts.AIAgent.Domain",
                    "IHostPro.Contexts.AIAgent.Application",
                    "IHostPro.Contexts.AIAgent.Infrastructure")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
