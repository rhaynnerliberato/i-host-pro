using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Api.Controllers;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards the two read-only catalog endpoints (Incremento 3, Checkpoint 3):
/// each controller action must require exactly its documented permission
/// policy, and neither controller may ever expose a write HTTP verb — the
/// role/permission catalog is platform-fixed in this phase (Documento 09
/// §18; <see cref="RolesController"/>/<see cref="PermissionsController"/>
/// doc comments). Plain reflection, not NetArchTest: these are per-action
/// attribute checks, not assembly-level dependency rules (see
/// <c>IdentityDependencyTests</c> for those).
/// </summary>
public class IdentityCatalogEndpointsArchitectureTests
{
    private static readonly Type[] ForbiddenWriteVerbAttributes =
    [
        typeof(HttpPostAttribute),
        typeof(HttpPutAttribute),
        typeof(HttpPatchAttribute),
        typeof(HttpDeleteAttribute),
    ];

    public static IEnumerable<object[]> ControllerActionPolicies() =>
        [
            [typeof(RolesController), nameof(RolesController.List), IdentityPermissionCodes.RolesRead],
            [typeof(PermissionsController), nameof(PermissionsController.List), IdentityPermissionCodes.PermissionsRead],
        ];

    [Theory]
    [MemberData(nameof(ControllerActionPolicies))]
    public void Action_requires_exactly_its_documented_permission_policy(Type controllerType, string actionName, string expectedPolicy)
    {
        var action = controllerType.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        action.Should().NotBeNull($"{controllerType.Name}.{actionName} must exist");

        var authorizeAttributes = action!.GetCustomAttributes<AuthorizeAttribute>().ToArray();

        authorizeAttributes.Should().ContainSingle(
            a => a.Policy == expectedPolicy,
            $"{controllerType.Name}.{actionName} must be protected by exactly the \"{expectedPolicy}\" policy, never left open or bound to a different one");
    }

    public static IEnumerable<object[]> CatalogControllerTypes() =>
        [
            [typeof(RolesController)],
            [typeof(PermissionsController)],
        ];

    [Theory]
    [MemberData(nameof(CatalogControllerTypes))]
    public void Controller_exposes_no_write_HTTP_verb(Type controllerType)
    {
        var actions = controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();

        actions.Should().NotBeEmpty($"{controllerType.Name} must expose at least one action");

        foreach (var action in actions)
        foreach (var forbiddenAttributeType in ForbiddenWriteVerbAttributes)
        {
            action.GetCustomAttributes(forbiddenAttributeType, inherit: false).Should().BeEmpty(
                $"{controllerType.Name}.{action.Name} must never accept {forbiddenAttributeType.Name} — the catalog is platform-fixed in this phase (Documento 09 §18)");
        }
    }
}
