using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Infrastructure;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Fase 11, Checkpoint 3 — Read Tools &amp; Context Builder. Proves the
/// architectural guarantees the mandate itself requires: exactly the 8
/// approved Read Tools exist (no more, no less); AIAgent's new cross-context
/// reach is scoped EXCLUSIVELY to each target Bounded Context's own
/// <c>.Application</c> layer (Exception #3) — never a purpose-limited
/// Contracts-tier reader belonging to a different consumer, never any
/// Domain/Infrastructure/Api layer; and the Worker gains exactly the Query
/// dispatch surface these Tools need, never the write Command dispatch
/// surface (mandate section 2/13 — "NÃO registrar indiscriminadamente no
/// Worker... write dispatch surface").
/// </summary>
public class AIAgentReadToolsArchitectureTests
{
    private static readonly Type[] ApprovedToolTypes =
    [
        typeof(GetReservationSummaryTool),
        typeof(GetScheduleTool),
        typeof(GetAvailabilityTool),
        typeof(GetPropertyInformationTool),
        typeof(GetAccessInstructionsTool),
        typeof(GetCleaningStatusTool),
        typeof(GetPaymentStatusTool),
        typeof(GetRelevantPoliciesTool),
    ];

    [Fact]
    public void Exactly_The_Eight_Approved_Read_Tools_Exist_No_More_No_Less()
    {
        var actualToolTypes = typeof(GetReservationSummaryTool).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAgentTool).IsAssignableFrom(t))
            .ToList();

        actualToolTypes.Should().BeEquivalentTo(ApprovedToolTypes,
            "Fase 11, Checkpoint 3 approved exactly these 8 Read Tools — adding a 9th (or a write tool) requires a new mandate");
    }

    /// <summary>
    /// Exception #3's own boundary: each Tool reaches its owning context
    /// EXCLUSIVELY through that context's <c>.Application</c> layer — never
    /// <c>.Domain</c>/<c>.Infrastructure</c>/<c>.Api</c>, and never
    /// <c>.Contracts</c> either (that is exactly the purpose-limited
    /// Contracts-tier reader mechanism this checkpoint deliberately avoids —
    /// e.g. <c>IReservationScheduleReader</c>/<c>IPropertyGuestAccessReader</c>
    /// stay purpose-limited to their own single consumer). Referencing a
    /// target context's own <c>.Contracts</c> assembly is NOT forbidden here
    /// — e.g. <c>EarlyCheckInPolicy</c>/<c>LateCheckoutPolicy</c> are plain,
    /// already-shared data shapes an Application Query's own public result
    /// legitimately exposes (Configuration's own Exception #1), not a
    /// purpose-limited reader for a single consumer; the NEXT test targets
    /// those two specific readers by name instead.
    /// </summary>
    [Fact]
    public void Infrastructure_Never_References_The_Domain_Infrastructure_Or_Api_Layer_Of_Any_Target_Bounded_Context()
    {
        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.Reservations.Domain", "IHostPro.Contexts.Reservations.Infrastructure", "IHostPro.Contexts.Reservations.Api",
            "IHostPro.Contexts.PropertyManagement.Domain", "IHostPro.Contexts.PropertyManagement.Infrastructure", "IHostPro.Contexts.PropertyManagement.Api",
            "IHostPro.Contexts.Housekeeping.Domain", "IHostPro.Contexts.Housekeeping.Infrastructure", "IHostPro.Contexts.Housekeeping.Api",
            "IHostPro.Contexts.Configuration.Domain", "IHostPro.Contexts.Configuration.Infrastructure", "IHostPro.Contexts.Configuration.Api",
            "IHostPro.Contexts.Payments.Domain", "IHostPro.Contexts.Payments.Infrastructure", "IHostPro.Contexts.Payments.Api",
        };

        var result = Types.InAssembly(typeof(GetReservationSummaryTool).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.AIAgent.Infrastructure.Tools")
            .Should()
            .NotHaveDependencyOnAny(forbiddenDependencies)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Read Tools must reach another Bounded Context exclusively through its .Application layer (Exception #3), never its Domain/Infrastructure/Api. Failing types: " +
            string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []));
    }

    /// <summary>
    /// The narrow, name-exact guard the mandate actually cares about:
    /// AIAgent must never reuse a purpose-limited Contracts-tier reader
    /// belonging to a different single consumer, even though referencing
    /// the surrounding .Contracts assembly itself is otherwise permitted
    /// (see the previous test's own doc comment).
    /// </summary>
    [Fact]
    public void Infrastructure_Never_References_A_Purpose_Limited_Reader_Reserved_For_A_Different_Consumer()
    {
        var result = Types.InAssembly(typeof(GetReservationSummaryTool).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.AIAgent.Infrastructure.Tools")
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Reservations.Contracts.IReservationScheduleReader",
                "IHostPro.Contexts.PropertyManagement.Contracts.IPropertyGuestAccessReader")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "IReservationScheduleReader (Guest Operations only) and IPropertyGuestAccessReader (Communication only) are purpose-limited to their own single consumer — AIAgent must reach the same underlying data through the owning context's own Application Query instead. Failing types: " +
            string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []));
    }

    [Fact]
    public void No_Anthropic_Or_Claude_Specific_Type_Exists_Anywhere_In_AIAgent()
    {
        var assemblies = new[]
        {
            typeof(IHostPro.Contexts.AIAgent.Domain.AgentSession).Assembly,
            typeof(IHostPro.Contexts.AIAgent.Application.ModelRequest).Assembly,
            typeof(GetReservationSummaryTool).Assembly,
        };

        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => (t.FullName ?? t.Name).Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
                        || (t.FullName ?? t.Name).Contains("Claude", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "IModelProvider/AvailableTools/ToolCallRequest are all provider-neutral by design — no Anthropic/Claude-specific type may exist yet (real integration is a future checkpoint's scope)");
    }

    /// <summary>
    /// Mandate section 2/13: the Worker must gain exactly the Query dispatch
    /// surface AIAgent's Read Tools need — never the write Command dispatch
    /// surface (validators, write pipeline behaviors, command executors),
    /// which remains Api-only via each context's own
    /// <c>Add&lt;Context&gt;CommandDispatch</c>.
    /// </summary>
    [Fact]
    public void Worker_Composition_Resolves_The_Five_Query_Dispatchers_But_Registers_No_Write_Command_Surface()
    {
        var services = BuildWorkerEquivalentServices();

        var expectedDispatchers = new[]
        {
            typeof(IReservationsRequestDispatcher), typeof(IPropertyManagementRequestDispatcher),
            typeof(IHousekeepingRequestDispatcher), typeof(IConfigurationRequestDispatcher),
            typeof(IPaymentsRequestDispatcher),
        };

        foreach (var dispatcherType in expectedDispatchers)
        {
            services.Any(d => d.ServiceType == dispatcherType).Should().BeTrue(
                $"{dispatcherType.Name} must be resolvable from the same composition the Worker uses (AddXModule), since AIAgent's Read Tools run in IHostPro.Worker");
        }

        var expectedHandlers = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Application.Reservations.GetReservationDetailQueryHandler),
            typeof(IHostPro.Contexts.Reservations.Application.Schedule.ListScheduleQueryHandler),
            typeof(IHostPro.Contexts.PropertyManagement.Application.Properties.GetPropertyDetailQueryHandler),
            typeof(IHostPro.Contexts.PropertyManagement.Application.GuestAccess.GetPropertyAccessConfigurationQueryHandler),
            typeof(IHostPro.Contexts.Housekeeping.Application.Cleanings.GetCleaningStatusByReservationQueryHandler),
            typeof(IHostPro.Contexts.Configuration.Application.Policies.GetEffectivePolicyQueryHandler),
            typeof(IHostPro.Contexts.Payments.Application.GetPaymentStatusByReservationQueryHandler),
        };

        foreach (var handlerType in expectedHandlers)
        {
            services.Any(d => d.ImplementationType == handlerType).Should().BeTrue(
                $"{handlerType.FullName}'s own registration must survive KeepOnlyMediatorHandlers — it is on the allowlist");
        }

        // One real write Command per target context — proves
        // Add<Context>CommandDispatch()'s own IValidator<TCommand>
        // registrations never leaked into the Worker's composition.
        var forbiddenCommandTypes = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Application.Reservations.CreateReservationCommand),
            typeof(IHostPro.Contexts.PropertyManagement.Application.Properties.CreatePropertyCommand),
            typeof(IHostPro.Contexts.Housekeeping.Application.Cleanings.CreateCleaningCommand),
            typeof(IHostPro.Contexts.Configuration.Application.Policies.CreatePolicyValueVersionCommand),
        };

        var offendingValidatorRegistrations = services
            .Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(FluentValidation.IValidator<>))
            .Where(d => forbiddenCommandTypes.Contains(d.ServiceType.GetGenericArguments()[0]))
            .Select(d => d.ServiceType.FullName)
            .ToList();

        offendingValidatorRegistrations.Should().BeEmpty(
            "the Worker's composition must never register a write-Command-only validator — that surface stays Api-only via Add<Context>CommandDispatch. Found: " +
            string.Join(", ", offendingValidatorRegistrations));
    }

    private static IServiceCollection BuildWorkerEquivalentServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Reservations"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:PropertyManagement"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:Housekeeping"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:Configuration"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:Payments"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:AIAgent"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        }).Build();

        var services = new ServiceCollection();

        services.AddScoped<IHostPro.BuildingBlocks.Infrastructure.Multitenancy.ITenantContext, IHostPro.BuildingBlocks.Infrastructure.Multitenancy.TenantContext>();
        services.AddIHostProTenantAwarePipeline();

        // Mirrors IHostPro.Worker/Program.cs's own registrations for the
        // five Bounded Contexts AIAgent's Read Tools call into — deliberately
        // NEVER calls any Add<Context>CommandDispatch() (Api-only). Each
        // AddXModule() call is immediately followed by KeepOnlyMediatorHandlers,
        // exactly like the real Program.cs — that trimming deliberately does
        // NOT live inside AddXModule itself (IHostPro.Api's own real write
        // HTTP endpoints call the very same shared method and need every
        // handler to stay registered there).
        services.AddReservationsModule(configuration);
        services.KeepOnlyMediatorHandlers(
            typeof(IHostPro.Contexts.Reservations.Application.Reservations.GetReservationDetailQueryHandler),
            typeof(IHostPro.Contexts.Reservations.Application.Schedule.ListScheduleQueryHandler));

        services.AddPropertyManagementModule(configuration, isDevelopmentEnvironment: false);
        services.KeepOnlyMediatorHandlers(
            typeof(IHostPro.Contexts.PropertyManagement.Application.Properties.GetPropertyDetailQueryHandler),
            typeof(IHostPro.Contexts.PropertyManagement.Application.GuestAccess.GetPropertyAccessConfigurationQueryHandler));

        services.AddHousekeepingModule(configuration);
        services.KeepOnlyMediatorHandlers(typeof(IHostPro.Contexts.Housekeeping.Application.Cleanings.GetCleaningStatusByReservationQueryHandler));

        services.AddConfigurationModule(configuration);
        services.KeepOnlyMediatorHandlers(typeof(IHostPro.Contexts.Configuration.Application.Policies.GetEffectivePolicyQueryHandler));

        services.AddPaymentsModule(configuration);
        services.KeepOnlyMediatorHandlers(typeof(IHostPro.Contexts.Payments.Application.GetPaymentStatusByReservationQueryHandler));

        return services;
    }

    /// <summary>
    /// Proves <see cref="MediatorHandlerAllowlistExtensions.KeepOnlyMediatorHandlers"/>
    /// actually removed the write Command handler CLASS registrations
    /// themselves (not merely their validators, already proven above) — the
    /// exact descriptors whose unresolvable constructor dependencies crashed
    /// the real Worker subprocess under <c>ValidateOnBuild=true</c> during
    /// CP3 homologation (<c>CreatePolicyValueVersionCommandHandler</c> could
    /// not resolve <c>IPolicyDefinitionReader</c>). Deliberately stays at the
    /// descriptor-inspection level, like <c>TenantAwareDbContextResolutionTests</c>'
    /// own precedent, rather than calling <c>BuildServiceProvider(validateOnBuild:true)</c>
    /// here — faithfully replicating the Worker's ENTIRE real composition
    /// root (Wolverine outbox wiring, Redis cache, logging, etc.) is exactly
    /// what the real dual-process E2E gate already does end-to-end; a
    /// hand-rolled partial replica would only prove this test's own
    /// incompleteness, not the Worker's real startup health.
    /// </summary>
    [Fact]
    public void Worker_Composition_Never_Registers_A_Write_Command_Handler_Class()
    {
        var services = BuildWorkerEquivalentServices();

        var forbiddenHandlerTypes = new[]
        {
            typeof(IHostPro.Contexts.Configuration.Application.Policies.CreatePolicyValueVersionCommandHandler),
            typeof(IHostPro.Contexts.Reservations.Application.Reservations.CreateReservationCommandHandler),
            typeof(IHostPro.Contexts.PropertyManagement.Application.Properties.CreatePropertyCommandHandler),
            typeof(IHostPro.Contexts.Housekeeping.Application.Cleanings.CreateCleaningCommandHandler),
        };

        var offendingHandlerRegistrations = services
            .Where(d => d.ImplementationType is not null && forbiddenHandlerTypes.Contains(d.ImplementationType))
            .Select(d => d.ImplementationType!.FullName)
            .Distinct()
            .ToList();

        offendingHandlerRegistrations.Should().BeEmpty(
            "KeepOnlyMediatorHandlers must remove every write Command handler's own registration — this is the exact class of descriptor whose unresolvable dependency crashed the real Worker subprocess (ValidateOnBuild=true) during CP3 homologation. Found: " +
            string.Join(", ", offendingHandlerRegistrations));
    }
}
