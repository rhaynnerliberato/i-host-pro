using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the Communication-specific rules from Fase 9, Checkpoint 1
/// (Comunicação e Integrações do MVP): Communication is a Supporting Bounded
/// Context that may reference ONLY Reservations.Contracts (the
/// <c>ReservationCreated</c> trigger and the ADR-019 guest-contact reader)
/// and Configuration.Contracts (the general Configuration &amp; Policy
/// synchronous-read exception, ADR-002, for <c>ITemplateReader</c>) — never
/// any other context's Domain/Application/Infrastructure/Api, and never any
/// other context's DbContext. Mirrors <c>DashboardDependencyTests</c> exactly
/// where applicable.
/// </summary>
public class CommunicationDependencyTests
{
    [Fact]
    public void Domain_Should_Not_Depend_On_Application_Infrastructure_Or_EfCore()
    {
        var result = Types.InAssembly(typeof(Message).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Communication.Application",
                "IHostPro.Contexts.Communication.Infrastructure",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types.InAssembly(typeof(ICommunicationMessageExecutionScope).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.Communication.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// CP1 mandate — Communication has no HTTP surface this checkpoint and
    /// no real connector yet (fake only), so its Application layer must
    /// never depend on EF Core, ASP.NET Core, or Wolverine (the transport
    /// mechanism Communication.Infrastructure alone is responsible for —
    /// mirrors <c>Workflow_Application_Never_Depends_On_Wolverine</c>).
    /// </summary>
    [Fact]
    public void Application_Should_Not_Depend_On_EfCore_AspNetCore_Or_Wolverine()
    {
        var result = Types.InAssembly(typeof(ICommunicationMessageExecutionScope).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Wolverine",
                "Wolverine.EntityFrameworkCore",
                "Wolverine.RabbitMQ")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// Communication.Application/.Infrastructure may reference other
    /// Bounded Contexts exclusively through the two approved Contracts
    /// assemblies (Reservations.Contracts — ReservationCreated + ADR-019;
    /// Configuration.Contracts — ADR-002) — never any Domain/Application/
    /// Infrastructure/Api layer of any context, including Reservations' and
    /// Configuration's own, and never PropertyManagement/Housekeeping/
    /// Identity/Dashboard/Workflow at all this checkpoint.
    /// </summary>
    [Fact]
    public void Application_And_Infrastructure_Never_Reference_Other_Contexts_Domain_Application_Infrastructure_Or_Api()
    {
        var assembliesToCheck = new[]
        {
            typeof(ICommunicationMessageExecutionScope).Assembly,
            typeof(CommunicationDbContext).Assembly,
        };

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.Reservations.Domain",
            "IHostPro.Contexts.Reservations.Application",
            "IHostPro.Contexts.Reservations.Infrastructure",
            "IHostPro.Contexts.Reservations.Api",
            "IHostPro.Contexts.Configuration.Domain",
            "IHostPro.Contexts.Configuration.Application",
            "IHostPro.Contexts.Configuration.Infrastructure",
            "IHostPro.Contexts.Configuration.Api",
            "IHostPro.Contexts.PropertyManagement",
            "IHostPro.Contexts.Housekeeping",
            "IHostPro.Contexts.Identity",
            "IHostPro.Contexts.Dashboard",
            "IHostPro.Contexts.Workflow",
        };

        foreach (var assembly in assembliesToCheck)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(forbiddenDependencies)
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    /// <summary>
    /// Architecture Principles' own closed-loop rule, generalized from
    /// <c>DashboardDependencyTests.No_Core_Bounded_Context_Ever_References_Dashboard</c>:
    /// Communication is a Supporting, leaf-consumer context — no other
    /// Bounded Context (Core or Supporting) may reference it back. Includes
    /// Dashboard and Workflow, both of which now exist (unlike when the
    /// Dashboard-side test was first written in Fase 7).
    /// </summary>
    [Fact]
    public void No_Other_Bounded_Context_Ever_References_Communication()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Reservations.Contracts.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly,
            typeof(IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext).Assembly,
            typeof(IHostPro.Contexts.Reservations.Api.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Contracts.CleaningCreated).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Contracts.PropertyCreated).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "IHostPro.Contexts.Communication.Domain",
                    "IHostPro.Contexts.Communication.Application",
                    "IHostPro.Contexts.Communication.Infrastructure")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    /// <summary>
    /// ADR-019's own testable consequence: Communication is the ONLY
    /// Bounded Context authorized to consume <c>IReservationGuestContactReader</c>
    /// — Reservations owns/implements it, everyone else must never reference
    /// it. Mirrors <c>WorkflowOrchestrationArchitectureTests.No_Other_Context_Assembly_References_CreateCleaningForReservation</c>
    /// exactly. Any other match means an unauthorized Bounded Context
    /// started reading guest PII outside the purpose-limited exception ADR-019
    /// approved.
    /// </summary>
    [Fact]
    public void No_Other_Context_Assembly_References_IReservationGuestContactReader_Except_Communication()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly,
        };

        var readerFullName = typeof(IHostPro.Contexts.Reservations.Contracts.IReservationGuestContactReader).FullName!;

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var referencingTypes = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(readerFullName)
                .GetTypes();

            referencingTypes.Should().BeEmpty(
                $"only Reservations (owner) and Communication (the sole authorized consumer, ADR-019) may " +
                $"reference IReservationGuestContactReader — {assembly.GetName().Name} referencing it would mean " +
                "an unauthorized Bounded Context is reading guest PII outside the purpose-limited exception");
        }
    }

    [Fact]
    public void CommunicationDbContext_Owns_The_Approved_Schema_Name()
    {
        using var dbContext = new CommunicationDbContextFactory().CreateDbContext([]);

        dbContext.SchemaName.Should().Be("communication");
    }

    /// <summary>
    /// CP1 mandate §16/§27: don't invent a <c>Conversation</c> or internal
    /// <c>Notification</c> entity just because the conceptual model has one
    /// — this checkpoint's single outbound use case only needs <c>Message</c>.
    /// The Notifications BC (internal Admin/Faxineira/Proprietário alerts)
    /// stays FUTURO — no such type belongs in Communication.Domain.
    /// </summary>
    [Fact]
    public void Domain_Declares_No_Conversation_Or_Notification_Type()
    {
        var forbiddenTypeNames = new[] { "Conversation", "Notification" };

        var domainTypeNames = Types.InAssembly(typeof(Message).Assembly)
            .GetTypes()
            .Select(t => t.Name)
            .ToList();

        foreach (var forbidden in forbiddenTypeNames)
        {
            domainTypeNames.Should().NotContain(forbidden,
                $"Communication.Domain must not declare a {forbidden} type this checkpoint (CP1 mandate — " +
                "no entity beyond what the single ReservationCreated -> WhatsApp use case needs)");
        }
    }

    /// <summary>
    /// Templates belong to Configuration &amp; Policy (CP1 mandate §18) —
    /// never a separate <c>IHostPro.Contexts.Templates.*</c> Bounded
    /// Context. <c>Template</c> must live inside Configuration.Domain.
    /// </summary>
    [Fact]
    public void Template_Lives_Inside_Configuration_Domain_Not_A_Separate_Templates_Context()
    {
        typeof(IHostPro.Contexts.Configuration.Domain.Template).Assembly.GetName().Name
            .Should().Be("IHostPro.Contexts.Configuration.Domain",
                "Templates are owned by Configuration & Policy (CP1 mandate) — no standalone Templates project exists");
    }

    /// <summary>
    /// Structural PII-absence guarantee (CP1 mandate §45): ReservationCreated
    /// must never gain a guest phone/name property just to make
    /// Communication's job easier — Communication obtains contact data
    /// exclusively through the ADR-019 synchronous reader, never through the
    /// event payload.
    /// </summary>
    [Fact]
    public void ReservationCreated_Never_Carries_Guest_Phone_Or_Name()
    {
        var propertyNames = typeof(IHostPro.Contexts.Reservations.Contracts.ReservationCreated)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        propertyNames.Should().NotContain(name => name.Contains("Phone", StringComparison.OrdinalIgnoreCase));
        propertyNames.Should().NotContain(name => name.Contains("GuestName", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
