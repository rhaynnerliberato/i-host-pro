using FluentAssertions;
using IHostPro.Contexts.Identity.Api.Controllers;
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

    [Fact]
    public void Application_Should_Not_Depend_On_AspNetCoreAuthorization()
    {
        // Incremento 3 plan, Checkpoint 1, approved layering: permission
        // reading and business rules live in Application, but the
        // ASP.NET Core authorization framework itself (IAuthorizationHandler,
        // IAuthorizationRequirement, AddAuthorizationBuilder) is exclusive to
        // Api — Application must stay just as framework-free for
        // authorization as it already is for ASP.NET Core Identity/EF Core/
        // Wolverine (see the other tests in this class).
        var result = Types.InAssembly(typeof(ITenantBootstrapReader).Assembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.AspNetCore.Authorization")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Infrastructure_Should_Not_Depend_On_AspNetCoreAuthorization()
    {
        // Incremento 3 plan, Checkpoint 1, approved layering: no
        // IAuthorizationHandler, IAuthorizationRequirement or
        // AddAuthorizationBuilder call may live in Identity.Infrastructure —
        // those belong exclusively to Identity.Api, which is the only project
        // allowed to depend on the ASP.NET Core authorization framework
        // (confirmed by Api_Does_Reference_AspNetCoreAuthorization below).
        var result = Types.InAssembly(typeof(IdentityModuleExtensions).Assembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.AspNetCore.Authorization")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Api_Does_Reference_AspNetCoreAuthorization()
    {
        // Confirms the isolation asserted by the two tests above is real (not
        // merely absent from Application/Infrastructure by accident):
        // PermissionRequirement/IdentityAuthorizationExtensions (Incremento 3
        // plan, Checkpoint 1) are expected to reference the framework
        // directly from Identity.Api.
        var typesDependingOnAuthorization = Types.InAssembly(typeof(AuthController).Assembly)
            .That()
            .HaveDependencyOn("Microsoft.AspNetCore.Authorization")
            .GetTypes();

        typesDependingOnAuthorization.Should().NotBeEmpty();
    }

    [Fact]
    public void Api_Should_Not_Depend_On_IdentityDbContext_Directly()
    {
        // Incremento 3 plan, Checkpoint 2, approved design: PermissionAuthorizationHandler
        // (and every other Api-layer type) reaches persisted data exclusively
        // through IPermissionReader (Application) — never by querying
        // IdentityDbContext itself, which would collapse the Api/Infrastructure
        // boundary Checkpoint 1 already established. Already impossible today
        // because Identity.Api does not even reference Identity.Infrastructure
        // as a project — this test makes that guarantee explicit and
        // self-enforcing even if a future change adds that project reference
        // for an unrelated reason.
        var result = Types.InAssembly(typeof(AuthController).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
