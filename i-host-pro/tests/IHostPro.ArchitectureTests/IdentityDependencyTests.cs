using FluentAssertions;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Contracts;
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
    public void Infrastructure_Should_Not_Depend_On_Wolverine_Transport_Or_Broker_Packages()
    {
        // Narrow, deliberate exception (Incremento 2 plan, Etapa 15A; Architecture
        // Principles §11): Identity.Infrastructure may reference
        // Wolverine.EntityFrameworkCore (IDbContextOutbox<TDbContext>) only —
        // never transport/broker/message-store configuration, which stays
        // exclusive to BuildingBlocks.Infrastructure and the Host processes.
        var result = Types.InAssembly(typeof(IdentityModuleExtensions).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Wolverine.RabbitMQ",
                "Wolverine.Postgresql",
                "Wolverine.RuntimeCompilation")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Contracts_Should_Not_Depend_On_Domain_Infrastructure_Or_Wolverine()
    {
        // Incremento 2 plan, Etapa 15: Identity.Contracts holds the six real
        // Integration Events (UserLoggedIn, etc.) — Architecture Principles
        // §13 requires it to be an immutable-DTO-only project other contexts
        // can safely reference directly, so it must never pull in Domain,
        // Infrastructure or any Wolverine assembly transitively.
        var result = Types.InAssembly(typeof(UserLoggedIn).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Domain",
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
