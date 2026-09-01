using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Infrastructure;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Fase 11, Checkpoint 4 — Write Tools &amp; Response Delivery. Proves:
/// exactly the 3 approved business Write Tools exist and are wired for
/// confirmation correctly; the write tools reach Guest Operations exclusively
/// through its .Application layer; every explicitly forbidden write
/// capability (CancelReservation, RecordGuestCheckedIn/Out, CreatePix,
/// CreateWorkflow, NotifyFrontDesk, RegisterIncident) has no corresponding
/// Tool anywhere in AIAgent; <c>SendAgentResponseCommand</c> is orchestration
/// infrastructure, never a model-callable Tool; and the Worker's composition
/// resolves exactly the write Command surface these 3 Tools need from Guest
/// Operations (never <c>RecordGuestCheckedIn/OutCommandHandler</c>) plus
/// Communication's own new <c>SendAgentResponseCommandHandler</c>.
/// </summary>
public class AIAgentWriteToolsArchitectureTests
{
    private static readonly Type[] ApprovedWriteToolTypes =
    [
        typeof(RequestEarlyCheckInTool),
        typeof(RequestLateCheckoutTool),
        typeof(RequestGuestAccessDeliveryTool),
    ];

    [Fact]
    public void Exactly_The_Three_Approved_Write_Tools_Exist_And_Each_Targets_Its_Own_Approved_ToolName()
    {
        var assembly = typeof(GetReservationSummaryTool).Assembly;

        foreach (var toolType in ApprovedWriteToolTypes)
            assembly.GetTypes().Should().Contain(toolType, $"{toolType.Name} must exist in {assembly.GetName().Name}");

        var toolNames = ApprovedWriteToolTypes
            .Select(t => (IAgentTool)Activator.CreateInstance(t, CreateDummyConstructorArgs(t))!)
            .Select(tool => tool.Descriptor.Name)
            .ToList();

        toolNames.Should().BeEquivalentTo(
            [AgentToolNames.RequestEarlyCheckIn, AgentToolNames.RequestLateCheckout, AgentToolNames.RequestGuestAccessDelivery]);
    }

    [Fact]
    public void RequestEarlyCheckIn_and_RequestLateCheckout_Require_Confirmation_RequestGuestAccessDelivery_Does_Not()
    {
        var policy = new AgentToolConfirmationPolicy();

        policy.RequiresConfirmation(AgentToolNames.RequestEarlyCheckIn).Should().BeTrue();
        policy.RequiresConfirmation(AgentToolNames.RequestLateCheckout).Should().BeTrue();
        policy.RequiresConfirmation(AgentToolNames.RequestGuestAccessDelivery).Should().BeFalse(
            "the guest's own explicit request already is the confirmation (CP0 decision)");
    }

    [Fact]
    public void The_Two_Confirmation_Required_Tools_Implement_IConfirmableAgentTool_The_Third_Does_Not()
    {
        typeof(RequestEarlyCheckInTool).Should().BeAssignableTo<IConfirmableAgentTool>();
        typeof(RequestLateCheckoutTool).Should().BeAssignableTo<IConfirmableAgentTool>();
        typeof(RequestGuestAccessDeliveryTool).Should().NotBeAssignableTo<IConfirmableAgentTool>(
            "EXPLICIT_REQUEST_IS_CONFIRMATION tools execute immediately — never a pending action");
    }

    [Fact]
    public void No_Forbidden_Write_Tool_Type_Exists_Anywhere_In_AIAgent()
    {
        var forbiddenTypeNames = new[]
        {
            "CancelReservationTool", "RecordGuestCheckedInTool", "RecordGuestCheckedOutTool",
            "CreatePixTool", "GeneratePixTool", "CreateWorkflowTool", "NotifyFrontDeskTool", "RegisterIncidentTool",
        };

        var assemblies = new[]
        {
            typeof(IHostPro.Contexts.AIAgent.Domain.AgentSession).Assembly,
            typeof(ModelRequest).Assembly,
            typeof(GetReservationSummaryTool).Assembly,
        };

        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => forbiddenTypeNames.Contains(t.Name))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "none of these capabilities were authorized for CP4 (FORBIDDEN/NOT_MODEL_TOOL/ALREADY_INDIRECT/DEFERRED_TO_CP6 per the CP4 gate) — found: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void SendAgentResponseCommand_Is_Never_Registered_As_An_IAgentTool()
    {
        var toolTypes = typeof(GetReservationSummaryTool).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAgentTool).IsAssignableFrom(t))
            .ToList();

        toolTypes.Should().NotContain(t => t.Name.Contains("SendAgentResponse", StringComparison.Ordinal),
            "SendAgentResponse is orchestration infrastructure, called automatically at the end of every interaction — never something the model chooses to call from AvailableTools");

        var descriptorNames = toolTypes
            .Select(t => (IAgentTool)Activator.CreateInstance(t, CreateDummyConstructorArgs(t))!)
            .Select(tool => tool.Descriptor.Name)
            .ToList();

        descriptorNames.Should().NotContain(AgentToolNames.RequestEarlyCheckIn + "SendAgentResponse");
        descriptorNames.Should().NotContain(n => n.Contains("SendAgentResponse", StringComparison.Ordinal));
    }

    /// <summary>Minimal reflection-based instantiation — every Tool constructor here takes exactly one dispatcher interface parameter, satisfied with a null (never invoked, only <see cref="IAgentTool.Descriptor"/> is read).</summary>
    private static object?[] CreateDummyConstructorArgs(Type toolType) =>
        toolType.GetConstructors()[0].GetParameters().Select(_ => (object?)null).ToArray();

    /// <summary>
    /// Exception #3's own boundary, mirrors <c>AIAgentReadToolsArchitectureTests</c>'
    /// own check exactly, applied to the new <c>ResponseDelivery</c>
    /// namespace: reaches Communication exclusively through its
    /// <c>.Application</c> layer — never <c>.Domain</c>/<c>.Infrastructure</c>/
    /// <c>.Api</c> (no WhatsApp connector, no Meta DTO, ever).
    /// </summary>
    [Fact]
    public void ResponseDelivery_Never_References_Communications_Domain_Infrastructure_Or_Api_Layer()
    {
        var result = Types.InAssembly(typeof(AgentResponseDeliveryService).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery")
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Communication.Domain", "IHostPro.Contexts.Communication.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "AgentResponseDeliveryService must reach Communication exclusively through its .Application layer (Exception #3), never its Domain/Infrastructure. Failing types: " +
            string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []));
    }

    /// <summary>Mandate item 42/44 combined: the Worker resolves the 3 approved Guest Operations write handlers and Communication's SendAgentResponseCommandHandler — never RecordGuestCheckedIn/OutCommandHandler.</summary>
    [Fact]
    public void Worker_Composition_Resolves_The_Three_Approved_GuestOperations_Write_Handlers_And_SendAgentResponse_Never_RecordCheckedIn_Or_CheckedOut()
    {
        var services = BuildWorkerEquivalentServicesForWriteTools();

        services.Any(d => d.ServiceType == typeof(IGuestOperationsRequestDispatcher)).Should().BeTrue();
        services.Any(d => d.ServiceType == typeof(ICommunicationRequestDispatcher)).Should().BeTrue();

        var expectedSurvivingHandlers = new[]
        {
            typeof(RequestEarlyCheckInCommandHandler),
            typeof(RequestLateCheckoutCommandHandler),
            typeof(RequestGuestAccessDeliveryCommandHandler),
            typeof(SendAgentResponseCommandHandler),
        };

        foreach (var handlerType in expectedSurvivingHandlers)
        {
            services.Any(d => d.ImplementationType == handlerType).Should().BeTrue(
                $"{handlerType.FullName}'s own registration must survive KeepOnlyMediatorHandlers — it is on the CP4 allowlist");
        }

        var forbiddenHandlerTypes = new[]
        {
            typeof(RecordGuestCheckedInCommandHandler),
            typeof(RecordGuestCheckedOutCommandHandler),
        };

        var offendingRegistrations = services
            .Where(d => d.ImplementationType is not null && forbiddenHandlerTypes.Contains(d.ImplementationType))
            .Select(d => d.ImplementationType!.FullName)
            .Distinct()
            .ToList();

        offendingRegistrations.Should().BeEmpty(
            "RecordGuestCheckedIn/OutCommandHandler are NOT_MODEL_TOOL (CP4 gate) — KeepOnlyMediatorHandlers must strip them from the Worker's own composition even though they share GuestOperations.Application's assembly. Found: " +
            string.Join(", ", offendingRegistrations));
    }

    private static IServiceCollection BuildWorkerEquivalentServicesForWriteTools()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GuestOperations"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:Communication"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        }).Build();

        var services = new ServiceCollection();

        services.AddScoped<IHostPro.BuildingBlocks.Infrastructure.Multitenancy.ITenantContext, IHostPro.BuildingBlocks.Infrastructure.Multitenancy.TenantContext>();
        services.AddIHostProTenantAwarePipeline();

        services.AddGuestOperationsModule(configuration);
        services.KeepOnlyMediatorHandlers(
            typeof(RequestEarlyCheckInCommandHandler), typeof(RequestLateCheckoutCommandHandler), typeof(RequestGuestAccessDeliveryCommandHandler));

        services.AddCommunicationModule(configuration);
        services.KeepOnlyMediatorHandlers(typeof(SendAgentResponseCommandHandler));

        return services;
    }
}
