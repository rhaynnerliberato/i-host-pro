using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NetArchTest.Rules;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards the four Condominium endpoints (Fase 2, Incremento 1, Checkpoint 2)
/// — <see cref="CondominiumsController"/> — mirroring
/// <see cref="IdentityUserAdministrationEndpointsArchitectureTests"/>'s
/// per-action policy check and exact-action-set check.
/// </summary>
public class PropertyManagementCondominiumsEndpointsArchitectureTests
{
    private static MethodInfo[] Actions() =>
        typeof(CondominiumsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();

    [Fact]
    public void Controller_exposes_exactly_the_four_approved_actions()
    {
        var actionNames = Actions().Select(m => m.Name).ToArray();

        actionNames.Should().BeEquivalentTo([
            nameof(CondominiumsController.Create),
            nameof(CondominiumsController.List),
            nameof(CondominiumsController.GetById),
            nameof(CondominiumsController.Update),
        ], "every administrative action must be explicitly approved before it exists here — no exclusion/archive/status endpoint may exist (Checkpoint 2 plan, item 1)");
    }

    [Fact]
    public void Every_action_requires_exactly_the_PROPERTIES_MANAGE_policy()
    {
        foreach (var action in Actions())
        {
            var authorizeAttributes = action.GetCustomAttributes<AuthorizeAttribute>().ToArray();

            authorizeAttributes.Should().ContainSingle(
                a => a.Policy == IdentityPermissionCodes.PropertiesManage,
                $"{action.Name} must be protected by exactly the \"{IdentityPermissionCodes.PropertiesManage}\" policy, " +
                "never left open, bound to a different policy, or reachable by plain authentication alone");
            action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty(
                $"{action.Name} must never be reachable anonymously");
        }
    }

    [Fact]
    public void No_request_contract_declares_a_tenantId_or_actorId_field()
    {
        // Checkpoint 2 plan, item 3: "não aceitar no request: tenantId;
        // actorId; createdBy; updatedBy; claims; role; permission." — the
        // actor/tenant come exclusively from PropertyManagementIdentityReader.
        string[] forbiddenFragments = ["tenantid", "actorid", "createdby", "updatedby", "claims", "role", "permission"];
        Type[] requestTypes = [typeof(CreateCondominiumRequest), typeof(UpdateCondominiumRequest), typeof(AddressRequest)];

        foreach (var requestType in requestTypes)
        {
            var propertyNames = requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

            propertyNames.Should().NotContain(
                name => forbiddenFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
                $"{requestType.Name} must never declare a tenant/actor/claims/role/permission field");
        }
    }

    [Fact]
    public void CondominiumSummaryResponse_never_carries_the_address()
    {
        // Checkpoint 2 plan, item 4: "não retornar endereço completo" in the listing.
        var propertyNames = typeof(CondominiumSummaryResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);

        propertyNames.Should().NotContain(
            name => name.Contains("address", StringComparison.OrdinalIgnoreCase),
            $"{nameof(CondominiumSummaryResponse)} must never carry the address");
    }

    [Fact]
    public void Exactly_the_known_approved_controllers_exist_no_Group_controller()
    {
        // Through Checkpoint 5: CondominiumsController (Checkpoint 2),
        // PropertiesController (Checkpoint 3 CRUD + Checkpoint 4 lifecycle +
        // Checkpoint 5 administrative Ownership), MyPropertiesController
        // (Checkpoint 5 self-service "mine"). Fase 10, Checkpoint 4 (Portaria
        // Notification Foundation) legitimately adds FrontDeskContactsController
        // — the front desk CONTACT (an operational recipient configuration)
        // is explicitly approved; a generic "Group" controller, or a
        // controller literally named for "Portaria" itself (as opposed to
        // its contact), still does not exist. See
        // PropertyManagementPropertiesEndpointsArchitectureTests for the
        // per-action rules.
        var controllerTypes = Types.InAssembly(typeof(CondominiumsController).Assembly)
            .That()
            .Inherit(typeof(ControllerBase))
            .GetTypes();

        controllerTypes.Select(t => t.Name).Should().BeEquivalentTo(
            [nameof(CondominiumsController), "PropertiesController", "MyPropertiesController", "FrontDeskContactsController"],
            "no Group controller may exist yet — only CondominiumsController/PropertiesController/MyPropertiesController (through Checkpoint 5) " +
            "and FrontDeskContactsController (Fase 10, Checkpoint 4)");
    }
}
