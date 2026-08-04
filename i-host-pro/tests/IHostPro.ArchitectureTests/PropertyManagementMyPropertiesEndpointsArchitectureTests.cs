using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Api.Controllers;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards the two self-service <c>mine</c> endpoints (Checkpoint 5 plan,
/// item 10) — <see cref="MyPropertiesController"/> — a separate controller
/// from <see cref="PropertiesController"/> (mirrors Identity's
/// <c>UsersController</c> vs. <c>UserAdministrationController</c> split).
/// </summary>
public class PropertyManagementMyPropertiesEndpointsArchitectureTests
{
    private static MethodInfo[] Actions() =>
        typeof(MyPropertiesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();

    [Fact]
    public void Controller_exposes_exactly_the_two_approved_actions()
    {
        var actionNames = Actions().Select(m => m.Name).ToArray();

        actionNames.Should().BeEquivalentTo([
            nameof(MyPropertiesController.List),
            nameof(MyPropertiesController.GetById),
        ], "no update, individual-link-detail, or ownerUserId-accepting action may exist here (Checkpoint 5 plan, restrictions)");
    }

    [Fact]
    public void Every_action_requires_exactly_the_PROPERTIES_READ_OWN_OWNER_policy()
    {
        // Never PROPERTIES:MANAGE — a different actor perspective from
        // PropertiesController entirely (Checkpoint 5 plan, item 1).
        foreach (var action in Actions())
        {
            var authorizeAttributes = action.GetCustomAttributes<AuthorizeAttribute>().ToArray();

            authorizeAttributes.Should().ContainSingle(
                a => a.Policy == IdentityPermissionCodes.PropertiesReadOwnOwner,
                $"{action.Name} must be protected by exactly the \"{IdentityPermissionCodes.PropertiesReadOwnOwner}\" policy");
            action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty(
                $"{action.Name} must never be reachable anonymously");
        }
    }

    [Fact]
    public void No_action_declares_an_ownerUserId_parameter()
    {
        // Checkpoint 5 plan, item 10: "não aceitar ownerUserId por query,
        // rota ou body" — the owner id comes exclusively from
        // PropertyManagementIdentityReader's validated claims.
        foreach (var action in Actions())
        {
            var parameterNames = action.GetParameters().Select(p => p.Name);

            parameterNames.Should().NotContain(
                name => name!.Contains("owner", StringComparison.OrdinalIgnoreCase),
                $"{action.Name} must never accept an owner id from the client");
        }
    }

    [Fact]
    public void The_ABAC_owner_filter_is_a_first_class_part_of_the_Query_contract_not_only_the_controller()
    {
        // Checkpoint 5 plan, item 10: "O filtro ABAC deve existir na
        // Query/Application, não apenas no controller." — proven structurally:
        // OwnerUserId is a required parameter of the Query itself, so the
        // filter cannot be bypassed by any caller that skips the controller.
        typeof(ListMyPropertiesQuery).GetProperties().Select(p => p.Name)
            .Should().Contain(nameof(ListMyPropertiesQuery.OwnerUserId));
        typeof(GetMyPropertyDetailQuery).GetProperties().Select(p => p.Name)
            .Should().Contain(nameof(GetMyPropertyDetailQuery.OwnerUserId));
    }
}
