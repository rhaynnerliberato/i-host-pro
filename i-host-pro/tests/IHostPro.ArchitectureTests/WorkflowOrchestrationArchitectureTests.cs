using System.Reflection;
using FluentAssertions;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Workflow.Application;
using IHostPro.Contexts.Workflow.Infrastructure.Messaging;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces ADR-018 (Fase 8, Checkpoint 1 — Workflow Orchestration, first
/// cross-context command) as a build-breaking guarantee rather than a
/// comment: the command/event distinction, that only Workflow Orchestration
/// may send <see cref="CreateCleaningForReservation"/>, that Workflow's own
/// Wolverine adapter stays a thin, stateless transport shim (no
/// <see cref="IServiceScopeFactory"/>/DbContext — approved Decision
/// Material 4, no <c>IWorkflowMessageExecutionScope</c> "just for
/// symmetry"), and — since Fase 8 Checkpoint 1.1's corrective review — that
/// the orchestration USE CASE lives in <c>Workflow.Application</c>, never
/// directly in the transport-only <c>Workflow.Infrastructure</c>.
/// </summary>
public class WorkflowOrchestrationArchitectureTests
{
    [Fact]
    public void CreateCleaningForReservation_Is_Never_An_IntegrationEvent()
    {
        // ADR-018's own central distinction: a cross-context command is a
        // request for the destination BC to do something, never a fact
        // that already happened. Never IntegrationEvent, never named like
        // one (e.g. "...RequestedEvent").
        typeof(IntegrationEvent).IsAssignableFrom(typeof(CreateCleaningForReservation)).Should().BeFalse(
            "CreateCleaningForReservation is a command, not a fact that already happened (ADR-018)");

        typeof(CreateCleaningForReservation).Name.Should().NotEndWith("Event")
            .And.NotContain("Requested", "a command must never be named/modeled like an Integration Event (ADR-018)");
    }

    [Fact]
    public void No_Other_Context_Assembly_References_CreateCleaningForReservation()
    {
        // ADR-018's own testable consequence: Workflow Orchestration is the
        // ONLY Bounded Context authorized to send this command, and
        // Housekeeping is the ONLY destination. None of the other real
        // context assemblies may reference the type at all — this would
        // fail the instant a second command-sending pattern appeared (e.g.
        // Reservations -> Housekeeping, Dashboard -> Housekeeping,
        // Housekeeping -> Reservations), exactly the forbidden shape
        // ADR-018 names explicitly. Mirrors
        // DashboardDependencyTests.Domain_Application_And_Infrastructure_Never_Depend_On_Identity's
        // own explicit-assembly-list pattern.
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly,
            typeof(IHostPro.Contexts.Reservations.Infrastructure.Messaging.CleaningCreatedHandler).Assembly,
            typeof(IHostPro.Contexts.Reservations.Api.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Application.IDashboardMessageExecutionScope).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Api.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Messaging.PolicyUpdatedHandler).Assembly,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var referencingTypes = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(typeof(CreateCleaningForReservation).FullName)
                .GetTypes();

            referencingTypes.Should().BeEmpty(
                $"only Housekeeping (owner) and Workflow (sender) may reference CreateCleaningForReservation — " +
                $"{assembly.GetName().Name} referencing it would mean an unauthorized Bounded Context is sending this command (ADR-018)");
        }
    }

    [Fact]
    public void Workflow_ReservationCreatedHandler_Never_Depends_On_ServiceScopeFactory_Or_DbContext()
    {
        // Approved Decision Material 4 (Fase 8, Checkpoint 1): Workflow is
        // stateless this checkpoint — no WorkflowDbContext, no aggregates,
        // no IWorkflowMessageExecutionScope "just for symmetry" with
        // Housekeeping/Reservations/Dashboard's own ADR-015/016 boundary,
        // since there is no tenant-scoped DbContext resolution for that
        // mechanism to protect here.
        var result = Types.InAssembly(typeof(ReservationCreatedHandler).Assembly)
            .That()
            .HaveName(nameof(ReservationCreatedHandler))
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
                "Wolverine.EntityFrameworkCore.IDbContextOutbox")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "ReservationCreatedHandler must stay a thin adapter with no scope-opening/persistence dependency (Decision Material 4)");
    }

    [Fact]
    public void Workflow_Infrastructure_Assembly_Declares_No_DbContext()
    {
        var dbContextTypes = Types.InAssembly(typeof(ReservationCreatedHandler).Assembly)
            .That()
            .Inherit(typeof(Microsoft.EntityFrameworkCore.DbContext))
            .GetTypes();

        dbContextTypes.Should().BeEmpty(
            "Workflow Orchestration is approved as stateless this checkpoint (Decision Material 4) — " +
            "no WorkflowDbContext, no schema, no migration");
    }

    [Fact]
    public void Exactly_The_Known_Approved_Types_Exist_In_Workflow_Infrastructure_Messaging()
    {
        // Single-entrypoint proof for Workflow's own transport surface, same
        // discipline already applied to Housekeeping/Reservations/
        // Dashboard's own adapters. Fase 8, Checkpoint 1.1: two of the five
        // types are the thin Wolverine adapter (ReservationCreatedHandler)
        // and WolverineWorkflowCommandDispatcher (the Infrastructure-side
        // implementation of Workflow.Application's IWorkflowCommandDispatcher)
        // — the orchestration use case itself moved OUT of this namespace,
        // into Workflow.Application. Fase 10, Checkpoint 1 (Guest Operations
        // Foundation) adds the third: GuestCheckedOutHandler, the thin
        // Wolverine adapter for Workflow's second trigger consumer. Fase 10,
        // Checkpoint 3 (Early Check-in / Late Checkout) adds the fourth and
        // fifth: EarlyCheckinApprovedHandler/LateCheckoutApprovedHandler, the
        // thin Wolverine adapters for Workflow's third and fourth trigger
        // consumers — updated explicitly, by exact expected count, rather
        // than merely relaxed to "any count," so an unapproved future
        // addition still fails this test the same way an unapproved
        // capability would.
        var handlerTypes = Types.InAssembly(typeof(ReservationCreatedHandler).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.Workflow.Infrastructure.Messaging")
            .GetTypes();

        handlerTypes.Should().HaveCount(5,
            "exactly five types are expected: the thin Wolverine adapters (ReservationCreatedHandler, " +
            "GuestCheckedOutHandler, EarlyCheckinApprovedHandler, LateCheckoutApprovedHandler) and " +
            "WolverineWorkflowCommandDispatcher, the transport-only implementation of IWorkflowCommandDispatcher");
    }

    // ---- Fase 8, Checkpoint 1.1 — Workflow.Application/.Infrastructure layering ----

    [Fact]
    public void Workflow_Application_Never_Depends_On_Wolverine()
    {
        var result = Types.InAssembly(typeof(IWorkflowCommandDispatcher).Assembly)
            .Should()
            .NotHaveDependencyOnAny("Wolverine", "Wolverine.EntityFrameworkCore", "Wolverine.RabbitMQ")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Workflow.Application is the use-case layer — it must never depend on Wolverine, the transport " +
            "mechanism Workflow.Infrastructure alone is responsible for (Checkpoint 1.1 corrective layering)");
    }

    [Fact]
    public void Workflow_Application_Never_Depends_On_EntityFrameworkCore()
    {
        var result = Types.InAssembly(typeof(IWorkflowCommandDispatcher).Assembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Workflow.Application must stay stateless and persistence-free — no DbContext, no EF Core dependency " +
            "at all (Decision Material 4, still true after the Application layer was introduced)");
    }

    [Fact]
    public void Workflow_Application_Never_Depends_On_Workflow_Infrastructure()
    {
        var infrastructureAssemblyName = typeof(ReservationCreatedHandler).Assembly.GetName().Name!;

        var result = Types.InAssembly(typeof(IWorkflowCommandDispatcher).Assembly)
            .Should()
            .NotHaveDependencyOn(infrastructureAssemblyName)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "the dependency must flow one way only — Infrastructure depends on Application's abstractions, " +
            "never the reverse");
    }

    [Fact]
    public void Workflow_Infrastructure_Depends_On_Workflow_Application()
    {
        // Types.Should().HaveDependencyOn(...) requires EVERY type in the
        // selection to carry the dependency — ReservationCreatedHandler
        // itself does not (it only needs the shared
        // IIntegrationEventHandler<T> abstraction, resolved via keyed DI),
        // so this checks the one Infrastructure type whose entire job IS
        // bridging to Workflow.Application.
        var applicationAssemblyName = typeof(IWorkflowCommandDispatcher).Assembly.GetName().Name!;

        var result = Types.InAssembly(typeof(ReservationCreatedHandler).Assembly)
            .That()
            .HaveName(nameof(WolverineWorkflowCommandDispatcher))
            .Should()
            .HaveDependencyOn(applicationAssemblyName)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "WolverineWorkflowCommandDispatcher must actually implement Workflow.Application's " +
            "IWorkflowCommandDispatcher — the whole point of the Checkpoint 1.1 layering split");
    }

    [Fact]
    public void No_Other_Context_Assembly_References_Workflow_Application_Or_Infrastructure()
    {
        // Mirrors No_Other_Context_Assembly_References_CreateCleaningForReservation
        // — Workflow Orchestration is a leaf consumer of other contexts'
        // Contracts, never something another context depends back on.
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly,
            typeof(IHostPro.Contexts.Reservations.Infrastructure.Messaging.CleaningCreatedHandler).Assembly,
            typeof(IHostPro.Contexts.Reservations.Api.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Application.Cleanings.IReservationCancellationGuard).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.HousekeepingModuleExtensions).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Application.IDashboardMessageExecutionScope).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Messaging.PolicyUpdatedHandler).Assembly,
        };

        var workflowAssemblyNames = new[]
        {
            typeof(IWorkflowCommandDispatcher).Assembly.GetName().Name!,
            typeof(ReservationCreatedHandler).Assembly.GetName().Name!,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            foreach (var workflowAssemblyName in workflowAssemblyNames)
            {
                var referencingTypes = Types.InAssembly(assembly)
                    .That()
                    .HaveDependencyOn(workflowAssemblyName)
                    .GetTypes();

                referencingTypes.Should().BeEmpty(
                    $"{assembly.GetName().Name} must never depend on {workflowAssemblyName} — Workflow Orchestration " +
                    "only ever consumes other contexts' Contracts, no other context depends back on it");
            }
        }
    }

    [Fact]
    public void Workflow_Application_Only_References_Other_Bounded_Contexts_Through_Contracts()
    {
        // Structural proof that Workflow.Application's cross-context
        // knowledge is limited to Reservations.Contracts/Housekeeping.Contracts
        // (the Integration Event it consumes and the command it produces) —
        // never those contexts' Domain/Application/Infrastructure/Api layers.
        var forbiddenAssemblyNames = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly.GetName().Name!,
            typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly.GetName().Name!,
            typeof(IHostPro.Contexts.Reservations.Infrastructure.Messaging.CleaningCreatedHandler).Assembly.GetName().Name!,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly.GetName().Name!,
            typeof(IHostPro.Contexts.Housekeeping.Application.Cleanings.IReservationCancellationGuard).Assembly.GetName().Name!,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.HousekeepingModuleExtensions).Assembly.GetName().Name!,
        };

        var result = Types.InAssembly(typeof(IWorkflowCommandDispatcher).Assembly)
            .Should()
            .NotHaveDependencyOnAny(forbiddenAssemblyNames)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Workflow.Application must reference other Bounded Contexts exclusively via their public Contracts " +
            "assemblies, never their Domain/Application/Infrastructure/Api layers");
    }

    // ---- Fase 10, Checkpoint 1 (Guest Operations Foundation) ----------------

    [Fact]
    public void CloseReservation_Is_Never_An_IntegrationEvent()
    {
        // Mirrors CreateCleaningForReservation_Is_Never_An_IntegrationEvent
        // exactly: a cross-context command is a request for the destination
        // BC to do something, never a fact that already happened.
        typeof(IntegrationEvent).IsAssignableFrom(typeof(CloseReservation)).Should().BeFalse(
            "CloseReservation is a command, not a fact that already happened");

        typeof(CloseReservation).Name.Should().NotEndWith("Event")
            .And.NotContain("Requested", "a command must never be named/modeled like an Integration Event");
    }

    [Fact]
    public void No_Other_Context_Assembly_References_CloseReservation()
    {
        // Mirrors No_Other_Context_Assembly_References_CreateCleaningForReservation:
        // Workflow Orchestration is the ONLY Bounded Context authorized to
        // send this command, and Reservations is the ONLY destination.
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Application.Cleanings.IReservationCancellationGuard).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.HousekeepingModuleExtensions).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Application.IDashboardMessageExecutionScope).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Api.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Messaging.PolicyUpdatedHandler).Assembly,
            typeof(IHostPro.Contexts.GuestOperations.Domain.GuestStayOperation).Assembly,
            typeof(GuestCheckedOut).Assembly,
            typeof(IHostPro.Contexts.GuestOperations.Infrastructure.GuestOperationsModuleExtensions).Assembly,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var referencingTypes = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(typeof(CloseReservation).FullName)
                .GetTypes();

            referencingTypes.Should().BeEmpty(
                $"only Reservations (owner) and Workflow (sender) may reference CloseReservation — " +
                $"{assembly.GetName().Name} referencing it would mean an unauthorized Bounded Context is sending this command");
        }
    }

    [Fact]
    public void GuestCheckedOut_Never_Declares_A_Forbidden_PII_Property()
    {
        // Mirrors Airbnb_Reservation_Events_Never_Declare_A_Forbidden_PII_Property:
        // GuestCheckedOut carries only ReservationId — no guest name/phone/
        // any other business-sensitive content.
        var forbiddenSubstrings = new[]
        {
            "Name", "Phone", "Email", "Payload", "Price", "Currency", "Payment", "Secret", "Token", "Credential",
        };

        var propertyNames = typeof(GuestCheckedOut)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        foreach (var forbidden in forbiddenSubstrings)
        {
            propertyNames.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"GuestCheckedOut must stay PII-safe/minimal — no property name may reference '{forbidden}'");
        }
    }
}
