using FluentAssertions;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the Dashboard &amp; Reporting-specific rules from Fase 7,
/// Incremento 2 (Dashboard &amp; Reporting Foundation), Checkpoint 0/1:
/// Dashboard may reference ONLY Reservations.Contracts/Housekeeping.Contracts/
/// PropertyManagement.Contracts (the events it consumes) — never their
/// Domain/Application/Infrastructure/Api, never Identity at all this
/// increment — plus the closed-loop, opposite-direction rule Architecture
/// Principles §14 states explicitly: "qualquer contexto Core dependendo de
/// ... Dashboard & Reporting ... (contextos terminais — apenas consomem
/// eventos)" is an absolute prohibition. Mirrors <c>ReservationsDependencyTests</c>
/// exactly where applicable.
/// </summary>
public class DashboardDependencyTests
{
    [Fact]
    public void Domain_Should_Not_Depend_On_Application_Infrastructure_Or_EfCore()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Dashboard.Application",
                "IHostPro.Contexts.Dashboard.Infrastructure",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Dashboard.Application.IDashboardMessageExecutionScope).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.Dashboard.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>Fase 7, Incremento 2, Checkpoint 2 — GetDashboardOverviewQuery/its validator/handler must never reference EF Core directly.</summary>
    [Fact]
    public void Application_Should_Not_Depend_On_EfCore()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Dashboard.Application.IDashboardMessageExecutionScope).Assembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// Fase 7, Incremento 2, Checkpoint 2 — Dashboard.Api may reference ONLY
    /// Identity.Contracts (IdentityPermissionCodes) — never Identity's
    /// Application/Infrastructure/Api, and never Dashboard.Infrastructure
    /// directly (mirrors ReservationsDependencyTests' own
    /// Api_Depends_On_Identity_Contracts_Only... test exactly).
    /// </summary>
    [Fact]
    public void Api_Depends_On_Identity_Contracts_Only_Never_Identity_Application_Infrastructure_Or_Api_And_Never_Dashboard_Infrastructure()
    {
        var apiAssembly = typeof(IHostPro.Contexts.Dashboard.Api.AssemblyReference).Assembly;

        var result = Types.InAssembly(apiAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Domain",
                "IHostPro.Contexts.Identity.Application",
                "IHostPro.Contexts.Identity.Infrastructure",
                "IHostPro.Contexts.Identity.Api",
                "IHostPro.Contexts.Dashboard.Infrastructure",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>Fase 7, Incremento 2, Checkpoint 2 — DashboardController delegates every query through the Mediator dispatcher, never touching DashboardDbContext.</summary>
    [Fact]
    public void Api_Never_References_DashboardDbContext()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Dashboard.Api.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Contracts_Should_Not_Depend_On_AspNetCore_Or_EfCore_Or_Domain_Or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Dashboard.Contracts.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "IHostPro.Contexts.Dashboard.Domain",
                "IHostPro.Contexts.Dashboard.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// Dashboard never references Identity at all this increment (Checkpoint
    /// 0 decision, §17 — no indicator needs it in the MVP).
    /// </summary>
    [Fact]
    public void Domain_Application_And_Infrastructure_Never_Depend_On_Identity()
    {
        var assembliesToCheck = new[]
        {
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Application.IDashboardMessageExecutionScope).Assembly,
            typeof(DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Contracts.AssemblyReference).Assembly,
        };

        foreach (var assembly in assembliesToCheck)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "IHostPro.Contexts.Identity.Domain",
                    "IHostPro.Contexts.Identity.Application",
                    "IHostPro.Contexts.Identity.Infrastructure",
                    "IHostPro.Contexts.Identity.Api",
                    "IHostPro.Contexts.Identity.Contracts")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    /// <summary>
    /// Dashboard.Infrastructure may reference ONLY the three approved
    /// Contracts projects (the events it consumes) — never any other
    /// context's Domain/Application/Infrastructure/Api (Architecture
    /// Principles §14: Dashboard is a terminal, event-consumer-only
    /// context — it reads no other context's persistence directly).
    /// </summary>
    [Fact]
    public void Infrastructure_Never_References_Other_Contexts_Domain_Application_Infrastructure_Or_Api()
    {
        var result = Types.InAssembly(typeof(DashboardDbContext).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Reservations.Domain",
                "IHostPro.Contexts.Reservations.Application",
                "IHostPro.Contexts.Reservations.Infrastructure",
                "IHostPro.Contexts.Reservations.Api",
                "IHostPro.Contexts.Housekeeping.Domain",
                "IHostPro.Contexts.Housekeeping.Application",
                "IHostPro.Contexts.Housekeeping.Infrastructure",
                "IHostPro.Contexts.Housekeeping.Api",
                "IHostPro.Contexts.PropertyManagement.Domain",
                "IHostPro.Contexts.PropertyManagement.Application",
                "IHostPro.Contexts.PropertyManagement.Infrastructure",
                "IHostPro.Contexts.PropertyManagement.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// The absolute prohibition Architecture Principles §14 states verbatim:
    /// "qualquer contexto Core dependendo de ... Dashboard &amp; Reporting ...
    /// (contextos terminais — apenas consomem eventos)". No Core Bounded
    /// Context (Reservations, Housekeeping, Property Management, Identity,
    /// Configuration) may reference any Dashboard assembly, in either
    /// direction — Dashboard is a read-model consumer, never a source of
    /// truth or a synchronous dependency for anyone else.
    /// </summary>
    [Fact]
    public void No_Core_Bounded_Context_Ever_References_Dashboard()
    {
        var coreAssemblies = new[]
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
        };

        foreach (var assembly in coreAssemblies.Distinct())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "IHostPro.Contexts.Dashboard.Domain",
                    "IHostPro.Contexts.Dashboard.Application",
                    "IHostPro.Contexts.Dashboard.Infrastructure",
                    "IHostPro.Contexts.Dashboard.Contracts")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    [Fact]
    public void DashboardDbContext_Owns_The_Approved_Schema_Name()
    {
        using var dbContext = new DashboardDbContextFactory().CreateDbContext([]);

        dbContext.SchemaName.Should().Be("dashboard");
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
