using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the Property Management-specific rules from the Fase 2,
/// Incremento 1 plan: total isolation from Identity except for two approved
/// exceptions — Api → Identity.Contracts (authorization policy constants,
/// Checkpoint 2 plan, item 2) and, since Checkpoint 5, Application →
/// Identity.Contracts (the synchronous Ownership eligibility contract,
/// Checkpoint 5 plan, item 3/11/20) — and the usual Clean Architecture
/// layering. Domain/Infrastructure/Contracts still carry ZERO Identity
/// reference of any kind. See <see cref="PropertyManagementCondominiumsEndpointsArchitectureTests"/>
/// for the Checkpoint 2 endpoint-specific rules.
/// </summary>
public class PropertyManagementDependencyTests
{
    [Fact]
    public void Domain_Should_Not_Depend_On_Application_Infrastructure_Api_Or_EfCore()
    {
        var result = Types.InAssembly(typeof(Property).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.PropertyManagement.Application",
                "IHostPro.Contexts.PropertyManagement.Infrastructure",
                "IHostPro.Contexts.PropertyManagement.Api",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure_Or_Api()
    {
        var result = Types.InAssembly(typeof(AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.PropertyManagement.Infrastructure",
                "IHostPro.Contexts.PropertyManagement.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Contracts_Should_Not_Depend_On_AspNetCore_Or_EfCore_Or_Domain_Or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.PropertyManagement.Contracts.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "IHostPro.Contexts.PropertyManagement.Domain",
                "IHostPro.Contexts.PropertyManagement.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Domain_Infrastructure_And_Contracts_Never_Depend_On_Identity()
    {
        // Checkpoint 2 plan, item 2/18 (unchanged by Checkpoint 5): Domain,
        // Infrastructure and Contracts must carry zero reference to any
        // Identity project — the two approved exceptions (Api and,
        // since Checkpoint 5, Application) are checked by their own
        // dedicated tests below, which also confirm each is limited to
        // Identity.Contracts specifically.
        var assembliesToCheck = new[]
        {
            typeof(Property).Assembly, // Domain
            typeof(PropertyManagementDbContext).Assembly, // Infrastructure
            typeof(IHostPro.Contexts.PropertyManagement.Contracts.AssemblyReference).Assembly, // Contracts
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

    [Fact]
    public void Application_Depends_On_Identity_Contracts_Only_Never_Application_Infrastructure_Or_Api()
    {
        // Checkpoint 5 plan, item 3/11/20: Application may reference
        // Identity.Contracts (IIdentityUserEligibilityReader/
        // IdentityUserEligibility/IdentityRoleCodes) — the one new approved
        // exception this checkpoint — but never Identity.Application/
        // Infrastructure/Api, mirroring Api's own equivalent test exactly.
        var applicationAssembly = typeof(AssemblyReference).Assembly;

        var result = Types.InAssembly(applicationAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Domain",
                "IHostPro.Contexts.Identity.Application",
                "IHostPro.Contexts.Identity.Infrastructure",
                "IHostPro.Contexts.Identity.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Never_References_IdentityDbContext()
    {
        var result = Types.InAssembly(typeof(AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Api_Depends_On_Identity_Contracts_Only_Never_Application_Infrastructure_Or_Api()
    {
        // Checkpoint 2 plan, item 2: "Adicionar referência: PropertyManagement.Api
        // → Identity.Contracts... Não permitir referências a:
        // Identity.Application; Identity.Infrastructure; Identity.Api;
        // IdentityDbContext." This is the one approved exception in the whole
        // Bounded Context.
        var apiAssembly = typeof(IHostPro.Contexts.PropertyManagement.Api.AssemblyReference).Assembly;

        var result = Types.InAssembly(apiAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Identity.Domain",
                "IHostPro.Contexts.Identity.Application",
                "IHostPro.Contexts.Identity.Infrastructure",
                "IHostPro.Contexts.Identity.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));

        // Whether the reference is actually used cannot be checked via
        // NetArchTest/reflection here: IdentityPermissionCodes.PropertiesManage
        // is a `const string`, and [Authorize(Policy = ...)] requires a
        // compile-time constant — the C# compiler inlines the literal value
        // directly into CondominiumsController's compiled IL, erasing any
        // metadata reference to IdentityPermissionCodes itself (confirmed
        // empirically: HaveDependencyOn found zero matches despite the
        // source clearly using it). PropertyManagementSourceConventionTests
        // verifies this at the source-text level instead, where the
        // constant reference is still visible.
    }

    [Fact]
    public void Api_Never_References_IdentityDbContext()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.PropertyManagement.Api.AssemblyReference).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// Fase 10, Checkpoint 4 mandate §4/§9/§38: <c>FrontDeskContact</c> must
    /// never carry guest data, an access credential, or a provider-specific
    /// identifier — only the minimal operational contact fields the mandate
    /// approved (DisplayName/PhoneNumber/IsActive).
    /// </summary>
    [Fact]
    public void FrontDeskContact_Never_Declares_Guest_Data_Access_Credential_Or_Provider_Specific_Fields()
    {
        var propertyNames = typeof(IHostPro.Contexts.PropertyManagement.Domain.FrontDeskContact)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        foreach (var forbidden in new[]
                 {
                     "GuestName", "GuestPhone", "GuestCount", "Credential", "Email", "Document", "Cpf", "Rg", "Passport",
                     "ProviderMessageId", "WabaId", "WhatsApp",
                 })
        {
            propertyNames.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"FrontDeskContact must never carry a property containing '{forbidden}'");
        }
    }

    [Fact]
    public void PropertyManagementDbContext_Owns_The_Approved_Schema_Name()
    {
        // No connection is opened just to read SchemaName — reuses the same
        // design-time-only factory `dotnet ef migrations add` itself uses.
        using var dbContext = new PropertyManagementDbContextFactory().CreateDbContext([]);

        dbContext.SchemaName.Should().Be("property_management");
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
