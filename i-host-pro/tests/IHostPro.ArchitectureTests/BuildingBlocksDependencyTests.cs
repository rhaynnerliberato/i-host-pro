using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using NetArchTest.Rules;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces, at build time, the dependency rules defined in
/// "documentacao do projeto/Architecture Principles.md", Section 4 (Clean
/// Architecture) and Section 12 (BuildingBlocks). As each Bounded Context is
/// implemented, its own set of rules (Domain has zero dependencies, Application
/// never references Infrastructure, no module references another module's
/// Domain/Application/Infrastructure) must be added here following the same
/// pattern.
/// </summary>
public class BuildingBlocksDependencyTests
{
    [Fact]
    public void Domain_Should_Not_Depend_On_Application_Or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Entity<>).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.BuildingBlocks.Application",
                "IHostPro.BuildingBlocks.Infrastructure",
                "IHostPro.BuildingBlocks.Messaging.Abstractions")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types.InAssembly(typeof(ICommand).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.BuildingBlocks.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void MessagingAbstractions_Should_Contain_No_Business_Vocabulary_Dependency()
    {
        // The generic IntegrationEvent envelope must never depend on Domain,
        // Application or Infrastructure — it is the one artifact every future
        // <Context>.Contracts project will depend on (Architecture Principles §13).
        var result = Types.InAssembly(typeof(IntegrationEvent).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.BuildingBlocks.Domain",
                "IHostPro.BuildingBlocks.Application",
                "IHostPro.BuildingBlocks.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Domain_And_Application_Should_Never_Reference_Wolverine()
    {
        // Explicit, automated enforcement of the messaging isolation mandated by
        // Architecture Principles §11 and ADR-004: no Bounded Context (and, today,
        // no BuildingBlocks project other than Infrastructure) may know the
        // concrete messaging library. Only BuildingBlocks.Infrastructure and the
        // Host processes may reference WolverineFx.*.
        var domainResult = Types.InAssembly(typeof(Entity<>).Assembly)
            .Should()
            .NotHaveDependencyOnAny("Wolverine")
            .GetResult();

        var applicationResult = Types.InAssembly(typeof(ICommand).Assembly)
            .Should()
            .NotHaveDependencyOnAny("Wolverine")
            .GetResult();

        domainResult.IsSuccessful.Should().BeTrue(BuildFailureMessage(domainResult));
        applicationResult.IsSuccessful.Should().BeTrue(BuildFailureMessage(applicationResult));
    }

    [Fact]
    public void Infrastructure_Multitenancy_Types_Should_Be_Free_Of_Business_Vocabulary()
    {
        // Smoke test for the BuildingBlocks inclusion criteria (Architecture
        // Principles §12): types placed here must not reference a concrete
        // Bounded Context assembly, none of which exist yet in Phase 0.
        var result = Types.InAssembly(typeof(ITenantContext).Assembly)
            .That()
            .ResideInNamespace("IHostPro.BuildingBlocks.Infrastructure.Multitenancy")
            .Should()
            .NotHaveDependencyOnAny("IHostPro.Contexts")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
