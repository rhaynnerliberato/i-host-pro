using FluentAssertions;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Workflow.Infrastructure.Messaging;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces ADR-018 (Fase 8, Checkpoint 1 — Workflow Orchestration, first
/// cross-context command) as a build-breaking guarantee rather than a
/// comment: the command/event distinction, that only Workflow Orchestration
/// may send <see cref="CreateCleaningForReservation"/>, and that Workflow's
/// own consumer stays a thin, stateless adapter (no
/// <see cref="IServiceScopeFactory"/>/DbContext — approved Decision
/// Material 4, no <c>IWorkflowMessageExecutionScope</c> "just for
/// symmetry").
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
    public void Exactly_One_Wolverine_Entrypoint_Consumes_ReservationCreated_In_Workflow_Assembly()
    {
        // Single-entrypoint proof for Workflow's own consumer, same
        // discipline already applied to Housekeeping/Reservations/
        // Dashboard's own adapters.
        var handlerTypes = Types.InAssembly(typeof(ReservationCreatedHandler).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.Workflow.Infrastructure.Messaging")
            .GetTypes();

        handlerTypes.Should().HaveCount(2,
            "exactly two types are expected: the thin Wolverine adapter (ReservationCreatedHandler) and the " +
            "IIntegrationEventHandler<ReservationCreated> implementation it delegates to (CreateCleaningOnReservationCreated)");
    }
}
