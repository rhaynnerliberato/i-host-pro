using FluentAssertions;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the Configuration &amp; Policy-specific rules from Fase 5,
/// Incremento 1 (Policy Engine Foundation) — total isolation from every
/// other Bounded Context except the one approved exception (Api →
/// Identity.Contracts, the authorization policy constants), plus the usual
/// Clean Architecture layering. Unlike Reservations, this context never
/// itself consumes another context synchronously — it is the one BEING
/// consumed (Architecture Principles §14, Exceção 1) — so there is no
/// "Application depends on X.Contracts" exception to test here. Mirrors
/// <c>ReservationsDependencyTests</c>'s structure.
/// </summary>
public class ConfigurationDependencyTests
{
    [Fact]
    public void Domain_Should_Not_Depend_On_Application_Infrastructure_Api_Or_EfCore()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Configuration.Domain.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Configuration.Application",
                "IHostPro.Contexts.Configuration.Infrastructure",
                "IHostPro.Contexts.Configuration.Api",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure_Or_Api()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Configuration.Application.IConfigurationRequestDispatcher).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Configuration.Infrastructure",
                "IHostPro.Contexts.Configuration.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Contracts_Should_Not_Depend_On_AspNetCore_Or_EfCore_Or_Domain_Or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Configuration.Contracts.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "IHostPro.Contexts.Configuration.Domain",
                "IHostPro.Contexts.Configuration.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Domain_Application_Infrastructure_And_Contracts_Never_Depend_On_Any_Other_Context()
    {
        // Only Api may reference another context at all (Identity.Contracts,
        // tested separately below) — every other layer of this context must
        // carry zero reference to Identity/PropertyManagement/Reservations.
        var assembliesToCheck = new[]
        {
            typeof(IHostPro.Contexts.Configuration.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Configuration.Application.IConfigurationRequestDispatcher).Assembly,
            typeof(ConfigurationDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Contracts.AssemblyReference).Assembly,
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
                    "IHostPro.Contexts.PropertyManagement.Contracts",
                    "IHostPro.Contexts.Reservations.Domain",
                    "IHostPro.Contexts.Reservations.Application",
                    "IHostPro.Contexts.Reservations.Infrastructure",
                    "IHostPro.Contexts.Reservations.Api",
                    "IHostPro.Contexts.Reservations.Contracts")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    [Fact]
    public void Api_Depends_On_Identity_Contracts_Only_Never_Application_Infrastructure_Or_Api_And_Never_Any_Other_Context()
    {
        var apiAssembly = typeof(IHostPro.Contexts.Configuration.Api.AssemblyReference).Assembly;

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
                "IHostPro.Contexts.PropertyManagement.Contracts",
                "IHostPro.Contexts.Reservations.Domain",
                "IHostPro.Contexts.Reservations.Application",
                "IHostPro.Contexts.Reservations.Infrastructure",
                "IHostPro.Contexts.Reservations.Api",
                "IHostPro.Contexts.Reservations.Contracts")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Api_Never_References_IdentityDbContext_Or_PropertyManagementDbContext_Or_ReservationsDbContext()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.Configuration.Api.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext",
                "IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext",
                "IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void ConfigurationDbContext_Owns_The_Approved_Schema_Name()
    {
        using var dbContext = new ConfigurationDbContextFactory().CreateDbContext([]);

        dbContext.SchemaName.Should().Be("configuration");
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
