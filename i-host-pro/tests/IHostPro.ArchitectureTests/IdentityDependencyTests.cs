using FluentAssertions;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Infrastructure;
using NetArchTest.Rules;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the Identity &amp; Access-specific rules from the Incremento 1 plan:
/// Domain has zero knowledge of ASP.NET Core Identity, EF Core or Wolverine;
/// Application never references Infrastructure; Api never references Domain
/// directly (it must go through Application) — Architecture Principles,
/// Section 4.
/// </summary>
public class IdentityDependencyTests
{
    [Fact]
    public void Domain_Should_Not_Depend_On_AspNetCoreIdentity_Or_EfCore_Or_Wolverine()
    {
        var result = Types.InAssembly(typeof(User).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore.Identity",
                "Microsoft.Extensions.Identity.Core",
                "Microsoft.EntityFrameworkCore",
                "Wolverine")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Domain_Should_Not_Depend_On_Identity_Application_Or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(User).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Application",
                "IHostPro.Contexts.Identity.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure_Or_AspNetCoreIdentity()
    {
        var result = Types.InAssembly(typeof(ITenantBootstrapReader).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Infrastructure",
                "Microsoft.AspNetCore.Identity",
                "Microsoft.Extensions.Identity.Core",
                "Microsoft.EntityFrameworkCore",
                "Wolverine")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Infrastructure_Does_Reference_AspNetCoreIdentity()
    {
        // Confirms the isolation asserted by the other tests in this class is
        // real (not merely absent from Domain/Application by accident):
        // Infrastructure is where the custom stores/hasher live and is
        // expected to reference the framework directly.
        var typesDependingOnIdentityCore = Types.InAssembly(typeof(IdentityModuleExtensions).Assembly)
            .That()
            .HaveDependencyOn("Microsoft.AspNetCore.Identity")
            .GetTypes();

        typesDependingOnIdentityCore.Should().NotBeEmpty();
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
