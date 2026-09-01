using System.Reflection;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure;
using IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Fase 11, Checkpoint 6 (Human Handoff, Safety &amp; Audit). Proves: no
/// <c>AIAgent.Api</c> project exists (CP6 mandate: <c>CreateAIAgentApiProject=false</c>);
/// AIAgent never stores/returns an administrator (or guest) phone number
/// anywhere in its own Domain/Application; the fixed
/// <see cref="AgentHumanHandoffReasonCode"/> catalog is closed; no generic
/// "handoff Tool" was ever added to the model-callable surface (classification
/// happens entirely server-side, via <see cref="IAgentHumanHandoffReasonClassifier"/>,
/// never a Tool the model chooses to call); <see cref="AdministratorNotificationService"/>
/// reaches Communication exclusively through its <c>.Application</c> layer
/// (Exception #3); and the Worker/Api compositions each resolve exactly the
/// write-Command surface they actually need — never the other host's own.
/// </summary>
public class AIAgentHumanHandoffArchitectureTests
{
    [Fact]
    public void No_AIAgent_Api_Project_Exists()
    {
        var loadedAssemblyNames = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).ToList();

        loadedAssemblyNames.Should().NotContain(
            "IHostPro.Contexts.AIAgent.Api",
            "CP6 mandate: CreateAIAgentApiProject=false — ResumeAgentSessionCommand's endpoint lives directly in IHostPro.Api");
    }

    [Fact]
    public void AIAgent_Domain_And_Application_Never_Define_A_Phone_Or_Destination_Field()
    {
        // CP6 mandate item 8/19/21/27 — AIAgent never resolves, stores, or
        // returns an administrator's (or a guest's) phone number; that stays
        // exclusively inside Communication's own AdministratorNotificationContact.
        var assemblies = new[] { typeof(AgentSession).Assembly, typeof(ModelRequest).Assembly };
        var suspiciousMemberNames = new[] { "Phone", "DestinationPhone", "GuestPhone", "AdministratorPhone" };

        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass || t.IsInterface)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(p => (Type: t, Member: p.Name)))
            .Where(x => suspiciousMemberNames.Any(name => x.Member.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Type.FullName}.{x.Member}")
            .ToList();

        offenders.Should().BeEmpty(
            "AIAgent must never store a phone/destination — Communication owns AdministratorNotificationContact end-to-end. Found: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void AgentHumanHandoffReasonCode_Catalog_Is_Exactly_The_Ten_Approved_Values()
    {
        var values = Enum.GetNames<AgentHumanHandoffReasonCode>();

        values.Should().BeEquivalentTo(
        [
            "ExplicitHumanRequest", "Refund", "Accident", "Police", "Negotiation",
            "SevereDamage", "SeriousComplaint", "AggressiveBehavior", "LowConfidence", "IntegrationFailure",
        ], "adding a new restricted reason requires a new mandate — never invented silently");
    }

    [Fact]
    public void No_Tool_Implements_A_Generic_Handoff_Capability()
    {
        // Classification is entirely server-side (IAgentHumanHandoffReasonClassifier
        // maps Intent -> ReasonCode) — the model never calls a Tool to
        // trigger, acknowledge, or resolve a handoff.
        var toolTypes = typeof(GetReservationSummaryTool).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAgentTool).IsAssignableFrom(t))
            .ToList();

        toolTypes.Should().NotContain(t => t.Name.Contains("Handoff", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdministratorNotificationService_Never_References_Communications_Domain_Infrastructure_Or_Api_Layer()
    {
        var result = Types.InAssembly(typeof(AdministratorNotificationService).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery")
            .Should()
            .NotHaveDependencyOnAny("IHostPro.Contexts.Communication.Domain", "IHostPro.Contexts.Communication.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "AdministratorNotificationService must reach Communication exclusively through its .Application layer (Exception #3), never its Domain/Infrastructure. Failing types: " +
            string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []));
    }

    [Fact]
    public void AIAgent_Application_And_Domain_Never_Reference_Identity()
    {
        // CP6 mandate item 26 — no new synchronous exception was authorized;
        // Communication resolves the administrator contact entirely on its
        // own, from TenantId alone, never via an Identity lookup relayed
        // through AIAgent.
        var assemblies = new[] { typeof(AgentSession).Assembly, typeof(ModelRequest).Assembly };

        var offenders = assemblies
            .SelectMany(a => a.GetReferencedAssemblies())
            .Where(a => a.Name is not null && a.Name.StartsWith("IHostPro.Contexts.Identity", StringComparison.Ordinal))
            .Select(a => a.Name)
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty("AIAgent must never reference Identity — found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Worker_Composition_Resolves_SendHumanHandoffNotification_But_Never_The_AdministratorContact_Management_Handlers()
    {
        var services = BuildWorkerEquivalentServices();

        services.Any(d => d.ImplementationType == typeof(SendHumanHandoffNotificationCommandHandler)).Should().BeTrue(
            "the Worker-hosted AI Agent orchestrator is the real caller of SendHumanHandoffNotificationCommand");

        var apiOnlyHandlerTypes = new[]
        {
            typeof(UpsertAdministratorNotificationContactCommandHandler),
            typeof(GetAdministratorNotificationContactQueryHandler),
        };

        var offendingRegistrations = services
            .Where(d => d.ImplementationType is not null && apiOnlyHandlerTypes.Contains(d.ImplementationType))
            .Select(d => d.ImplementationType!.FullName)
            .Distinct()
            .ToList();

        offendingRegistrations.Should().BeEmpty(
            "administrator-contact management is an Api-only administrative concern — never resolvable from the Worker's own composition. Found: " +
            string.Join(", ", offendingRegistrations));
    }

    [Fact]
    public void Api_Composition_Resolves_AdministratorContact_Management_And_Resume_But_Never_SendAgentResponse_Or_SendHumanHandoffNotification()
    {
        var services = BuildApiEquivalentServices();

        var apiHandlerTypes = new[]
        {
            typeof(UpsertAdministratorNotificationContactCommandHandler),
            typeof(GetAdministratorNotificationContactQueryHandler),
        };

        foreach (var handlerType in apiHandlerTypes)
        {
            services.Any(d => d.ImplementationType == handlerType).Should().BeTrue(
                $"{handlerType.FullName} is Api's own administrative endpoint — it must resolve here");
        }

        services.Any(d => d.ServiceType == typeof(IAIAgentRequestDispatcher)).Should().BeTrue(
            "ResumeAgentSessionCommand's own dispatcher must resolve here — IHostPro.Api hosts the Resume endpoint");

        var workerOnlyHandlerTypes = new[]
        {
            typeof(SendAgentResponseCommandHandler),
            typeof(SendHumanHandoffNotificationCommandHandler),
        };

        var offendingRegistrations = services
            .Where(d => d.ImplementationType is not null && workerOnlyHandlerTypes.Contains(d.ImplementationType))
            .Select(d => d.ImplementationType!.FullName)
            .Distinct()
            .ToList();

        offendingRegistrations.Should().BeEmpty(
            "response/notification delivery needs a real IOutboundMessageConnector, which IHostPro.Api never registers — these handlers must never resolve here. Found: " +
            string.Join(", ", offendingRegistrations));
    }

    private static IServiceCollection BuildWorkerEquivalentServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Communication"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        }).Build();

        var services = new ServiceCollection();
        services.AddScoped<IHostPro.BuildingBlocks.Infrastructure.Multitenancy.ITenantContext, IHostPro.BuildingBlocks.Infrastructure.Multitenancy.TenantContext>();
        services.AddIHostProTenantAwarePipeline();

        services.AddCommunicationModule(configuration);
        services.KeepOnlyMediatorHandlers(typeof(SendAgentResponseCommandHandler), typeof(SendHumanHandoffNotificationCommandHandler));

        return services;
    }

    private static IServiceCollection BuildApiEquivalentServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AIAgent"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:Communication"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        }).Build();

        var services = new ServiceCollection();
        services.AddScoped<IHostPro.BuildingBlocks.Infrastructure.Multitenancy.ITenantContext, IHostPro.BuildingBlocks.Infrastructure.Multitenancy.TenantContext>();
        services.AddIHostProTenantAwarePipeline();

        services.AddAIAgentCommandDispatch(configuration);

        services.AddCommunicationModule(configuration);
        services.KeepOnlyMediatorHandlers(
            typeof(UpsertAdministratorNotificationContactCommandHandler), typeof(GetAdministratorNotificationContactQueryHandler));

        return services;
    }
}
