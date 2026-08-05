using FluentAssertions;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the Reservations-specific rules from the Fase 3, Incremento 1
/// plan: total isolation from Identity except for the one approved exception
/// (Api → Identity.Contracts, the authorization policy constant) and total
/// isolation from Property Management except for the one approved exception
/// (Application → PropertyManagement.Contracts, the
/// <c>IPropertyReservationEligibilityReader</c> port) — plus the usual Clean
/// Architecture layering. Mirrors <c>PropertyManagementDependencyTests</c>
/// exactly.
/// </summary>
public class ReservationsDependencyTests
{
    [Fact]
    public void Domain_Should_Not_Depend_On_Application_Infrastructure_Api_Or_EfCore()
    {
        var result = Types.InAssembly(typeof(Reservation).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Reservations.Application",
                "IHostPro.Contexts.Reservations.Infrastructure",
                "IHostPro.Contexts.Reservations.Api",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure_Or_Api()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Reservations.Infrastructure",
                "IHostPro.Contexts.Reservations.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Contracts_Should_Not_Depend_On_AspNetCore_Or_EfCore_Or_Domain_Or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Reservations.Contracts.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "IHostPro.Contexts.Reservations.Domain",
                "IHostPro.Contexts.Reservations.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Domain_Infrastructure_And_Contracts_Never_Depend_On_Identity_Or_PropertyManagement()
    {
        // Domain, Infrastructure and Contracts must carry zero reference to
        // Identity or Property Management — the two approved exceptions
        // (Api→Identity.Contracts, Application→PropertyManagement.Contracts)
        // are checked by their own dedicated tests below.
        var assembliesToCheck = new[]
        {
            typeof(Reservation).Assembly, // Domain
            typeof(ReservationsDbContext).Assembly, // Infrastructure
            typeof(IHostPro.Contexts.Reservations.Contracts.AssemblyReference).Assembly, // Contracts
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
                    "IHostPro.Contexts.Identity.Contracts",
                    "IHostPro.Contexts.PropertyManagement.Domain",
                    "IHostPro.Contexts.PropertyManagement.Application",
                    "IHostPro.Contexts.PropertyManagement.Infrastructure",
                    "IHostPro.Contexts.PropertyManagement.Api",
                    "IHostPro.Contexts.PropertyManagement.Contracts")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    [Fact]
    public void Application_Depends_On_PropertyManagement_Contracts_Only_Never_Application_Infrastructure_Or_Api()
    {
        // Fase 3, Incremento 1 plan, item 4: Application may reference
        // PropertyManagement.Contracts (IPropertyReservationEligibilityReader/
        // PropertyReservationEligibility) — the one approved exception —
        // but never PropertyManagement.Application/Infrastructure/Api, and
        // never any Identity project at all.
        var applicationAssembly = typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly;

        var result = Types.InAssembly(applicationAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.PropertyManagement.Domain",
                "IHostPro.Contexts.PropertyManagement.Application",
                "IHostPro.Contexts.PropertyManagement.Infrastructure",
                "IHostPro.Contexts.PropertyManagement.Api",
                "IHostPro.Contexts.Identity.Domain",
                "IHostPro.Contexts.Identity.Application",
                "IHostPro.Contexts.Identity.Infrastructure",
                "IHostPro.Contexts.Identity.Api",
                "IHostPro.Contexts.Identity.Contracts")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Never_References_PropertyManagementDbContext_Or_IdentityDbContext()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Reservations.Application.Optional<>).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext",
                "IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Api_Depends_On_Identity_Contracts_Only_Never_Application_Infrastructure_Or_Api_And_Never_PropertyManagement()
    {
        var apiAssembly = typeof(IHostPro.Contexts.Reservations.Api.AssemblyReference).Assembly;

        var result = Types.InAssembly(apiAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Domain",
                "IHostPro.Contexts.Identity.Application",
                "IHostPro.Contexts.Identity.Infrastructure",
                "IHostPro.Contexts.Identity.Api",
                "IHostPro.Contexts.PropertyManagement.Domain",
                "IHostPro.Contexts.PropertyManagement.Application",
                "IHostPro.Contexts.PropertyManagement.Infrastructure",
                "IHostPro.Contexts.PropertyManagement.Api",
                "IHostPro.Contexts.PropertyManagement.Contracts")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Api_Never_References_IdentityDbContext_Or_PropertyManagementDbContext()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Reservations.Api.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext",
                "IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void ReservationsDbContext_Owns_The_Approved_Schema_Name()
    {
        using var dbContext = new ReservationsDbContextFactory().CreateDbContext([]);

        dbContext.SchemaName.Should().Be("reservations");
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
