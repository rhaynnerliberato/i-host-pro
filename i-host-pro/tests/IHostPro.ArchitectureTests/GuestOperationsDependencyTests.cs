using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the Guest Operations-specific rules from Fase 10, Checkpoint 1
/// (Guest Operations Foundation).
/// </summary>
public class GuestOperationsDependencyTests
{
    [Fact]
    public void GuestStayOperation_Is_Tenant_Owned()
    {
        typeof(ITenantOwned).IsAssignableFrom(typeof(GuestStayOperation)).Should().BeTrue(
            "GuestStayOperation must implement ITenantOwned for the Global Query Filter + RLS to apply");
    }

    [Fact]
    public void GuestOperationsDbContext_Owns_The_Approved_Schema_Name()
    {
        // GuestOperationsDbContext requires a real ITenantContext/connection
        // string via DI in this checkpoint's shape (no parameterless design-time
        // factory exists, mirrors Reservations/Housekeeping) — SchemaName is a
        // plain constant property with no dependency on either, so a throwaway,
        // unconfigured instance is enough to read it.
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<GuestOperationsDbContext>().Options;
        using var dbContext = new GuestOperationsDbContext(
            options, new IHostPro.BuildingBlocks.Infrastructure.Multitenancy.TenantContext());

        dbContext.SchemaName.Should().Be("guest_operations");
    }

    [Fact]
    public void Domain_Never_Depends_On_Application_Infrastructure_Or_EfCore()
    {
        var result = Types.InAssembly(typeof(GuestStayOperation).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.GuestOperations.Application",
                "IHostPro.Contexts.GuestOperations.Infrastructure",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Never_Depends_On_Infrastructure()
    {
        var result = Types.InAssembly(typeof(IGuestOperationsTransactionExecutor).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.GuestOperations.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// Fase 10, Checkpoint 2 — Check-in/Checkout Core added the first two
    /// public endpoints, so <c>GuestOperations.Api</c> now exists (CP1 had
    /// zero endpoints and deliberately skipped the project — mirrors
    /// Workflow.Infrastructure's own precedent). Mirrors
    /// <c>ReservationsDependencyTests.Api_Depends_On_Identity_Contracts_Only_Never_Application_Infrastructure_Or_Api_And_Never_PropertyManagement</c>:
    /// the Api project may reference Application/Identity.Contracts only,
    /// never Infrastructure or any other Bounded Context's internals.
    /// </summary>
    [Fact]
    public void Api_Depends_On_Application_And_Identity_Contracts_Only_Never_Infrastructure_Or_Identity_Internals()
    {
        var apiAssembly = typeof(IHostPro.Contexts.GuestOperations.Api.AssemblyReference).Assembly;

        var result = Types.InAssembly(apiAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.GuestOperations.Infrastructure",
                "IHostPro.Contexts.Identity.Domain",
                "IHostPro.Contexts.Identity.Application",
                "IHostPro.Contexts.Identity.Infrastructure",
                "IHostPro.Contexts.Identity.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// Fase 10, Checkpoint 3 — Early Check-in / Late Checkout (ADR-024
    /// amendment, synchronous exceptions #7/#8): GuestOperations.Application/
    /// .Infrastructure may reference other Bounded Contexts exclusively
    /// through three approved Contracts assemblies (Reservations.Contracts —
    /// ReservationCreated + IReservationScheduleReader; Housekeeping.Contracts —
    /// ICleaningReadinessReader; Configuration.Contracts — the universal
    /// Configuration &amp; Policy exception, IEarlyCheckInPolicyReader/
    /// ILateCheckoutPolicyReader) — never any Domain/Application/
    /// Infrastructure/Api layer of any context, including Reservations'/
    /// Housekeeping's/Configuration's own, and never PropertyManagement/
    /// Identity/Dashboard/Workflow/Communication/ExternalIntegrations at all.
    /// Mirrors <c>CommunicationDependencyTests.Application_And_Infrastructure_Never_Reference_Other_Contexts_Domain_Application_Infrastructure_Or_Api</c>
    /// exactly.
    /// </summary>
    [Fact]
    public void Application_And_Infrastructure_Only_Reference_Reservations_Housekeeping_Configuration_Contracts()
    {
        var assembliesToCheck = new[]
        {
            typeof(IGuestOperationsTransactionExecutor).Assembly,
            typeof(GuestOperationsDbContext).Assembly,
        };

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.Reservations.Domain",
            "IHostPro.Contexts.Reservations.Application",
            "IHostPro.Contexts.Reservations.Infrastructure",
            "IHostPro.Contexts.Reservations.Api",
            "IHostPro.Contexts.Housekeeping.Domain",
            "IHostPro.Contexts.Housekeeping.Application",
            "IHostPro.Contexts.Housekeeping.Infrastructure",
            "IHostPro.Contexts.Housekeeping.Api",
            "IHostPro.Contexts.Configuration.Domain",
            "IHostPro.Contexts.Configuration.Application",
            "IHostPro.Contexts.Configuration.Infrastructure",
            "IHostPro.Contexts.Configuration.Api",
            "IHostPro.Contexts.PropertyManagement",
            "IHostPro.Contexts.Identity",
            "IHostPro.Contexts.Dashboard",
            "IHostPro.Contexts.Workflow",
            "IHostPro.Contexts.Communication",
            "IHostPro.Contexts.ExternalIntegrations",
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
    /// ADR-024 amendment synchronous exception #7's own testable consequence
    /// (Fase 10, Checkpoint 3): Guest Operations is the ONLY Bounded Context
    /// authorized to consume <c>IReservationScheduleReader</c> — Reservations
    /// owns/implements it, everyone else (including Housekeeping, the other
    /// new exception's owner) must never reference it. Mirrors
    /// <c>CommunicationDependencyTests.No_Other_Context_Assembly_References_IReservationGuestContactReader_Except_Communication</c>
    /// exactly.
    /// </summary>
    [Fact]
    public void No_Other_Context_Assembly_References_IReservationScheduleReader_Except_GuestOperations()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Application.IHousekeepingTransactionExecutor).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Communication.Domain.Message).Assembly,
            typeof(IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContext).Assembly,
            typeof(IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.ExternalIntegrationsDbContext).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly,
        };

        var readerFullName = typeof(IHostPro.Contexts.Reservations.Contracts.IReservationScheduleReader).FullName!;

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var referencingTypes = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(readerFullName)
                .GetTypes();

            referencingTypes.Should().BeEmpty(
                "only Reservations (owner) and Guest Operations (the sole authorized consumer, ADR-024 amendment " +
                $"exception #7) may reference IReservationScheduleReader — {assembly.GetName().Name} referencing " +
                "it would mean an unauthorized Bounded Context is reading Reservation schedule data outside the " +
                "purpose-limited exception");
        }
    }

    /// <summary>
    /// ADR-024 amendment synchronous exception #8's own testable consequence
    /// (Fase 10, Checkpoint 3): Guest Operations is the ONLY Bounded Context
    /// authorized to consume <c>ICleaningReadinessReader</c> — Housekeeping's
    /// first-ever synchronous exception, owns/implements it, everyone else
    /// (including Reservations, the other new exception's owner) must never
    /// reference it. Mirrors the <c>IReservationScheduleReader</c> test above
    /// exactly.
    /// </summary>
    [Fact]
    public void No_Other_Context_Assembly_References_ICleaningReadinessReader_Except_GuestOperations()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly,
            typeof(IHostPro.Contexts.Reservations.Infrastructure.Messaging.CleaningCreatedHandler).Assembly,
            typeof(IHostPro.Contexts.Reservations.Api.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Communication.Domain.Message).Assembly,
            typeof(IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContext).Assembly,
            typeof(IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.ExternalIntegrationsDbContext).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly,
        };

        var readerFullName = typeof(IHostPro.Contexts.Housekeeping.Contracts.ICleaningReadinessReader).FullName!;

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var referencingTypes = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(readerFullName)
                .GetTypes();

            referencingTypes.Should().BeEmpty(
                "only Housekeeping (owner) and Guest Operations (the sole authorized consumer, ADR-024 amendment " +
                $"exception #8) may reference ICleaningReadinessReader — {assembly.GetName().Name} referencing it " +
                "would mean an unauthorized Bounded Context is reading cleaning readiness data outside the " +
                "purpose-limited exception");
        }
    }

    /// <summary>
    /// No other Bounded Context may reference Guest Operations' internal
    /// layers — Workflow (the only current consumer) reaches it exclusively
    /// through <see cref="GuestOperations.Contracts"/> (see
    /// <c>WorkflowOrchestrationArchitectureTests.Workflow_Application_Only_References_Other_Bounded_Contexts_Through_Contracts</c>,
    /// which already covers the positive direction).
    /// </summary>
    [Fact]
    public void No_Other_Bounded_Context_Ever_References_GuestOperations_Domain_Application_Or_Infrastructure()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly,
            typeof(IHostPro.Contexts.Reservations.Infrastructure.Messaging.CleaningCreatedHandler).Assembly,
            typeof(IHostPro.Contexts.Reservations.Api.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.HousekeepingModuleExtensions).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Messaging.PolicyUpdatedHandler).Assembly,
            typeof(IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.ExternalIntegrationsDbContext).Assembly,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "IHostPro.Contexts.GuestOperations.Domain",
                    "IHostPro.Contexts.GuestOperations.Application",
                    "IHostPro.Contexts.GuestOperations.Infrastructure")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    [Fact]
    public void Only_The_Known_Approved_Migration_Exists()
    {
        var migrationsDirectory = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "GuestOperations",
            "IHostPro.Contexts.GuestOperations.Infrastructure", "Persistence", "Migrations");

        Directory.Exists(migrationsDirectory).Should().BeTrue($"expected {migrationsDirectory} to exist");

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith(".Designer", StringComparison.Ordinal))
            .ToArray();

        migrationFiles.Should().ContainSingle(name => name!.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", ".."));

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
