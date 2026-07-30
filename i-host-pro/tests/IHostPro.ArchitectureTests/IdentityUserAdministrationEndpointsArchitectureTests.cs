using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Controllers;
using IHostPro.Contexts.Identity.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards the nine administrative user endpoints (Incremento 3, Checkpoints
/// 5-9) — <see cref="UserAdministrationController"/> — mirroring
/// <see cref="IdentityCatalogEndpointsArchitectureTests"/>'s per-action policy
/// check (Checkpoint 3) and <see cref="IdentityUsersEndpointsArchitectureTests"/>'s
/// exact-action-set check (Checkpoint 4): every action must require exactly
/// the <see cref="IdentityPermissionCodes.UsersManage"/> policy, and no action
/// beyond the nine approved ones may exist. Plain reflection, not NetArchTest:
/// these are per-action attribute checks, not assembly-level dependency rules.
/// </summary>
public class IdentityUserAdministrationEndpointsArchitectureTests
{
    private static MethodInfo[] Actions() =>
        typeof(UserAdministrationController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();

    [Fact]
    public void Controller_exposes_exactly_the_nine_approved_administrative_actions()
    {
        var actionNames = Actions().Select(m => m.Name).ToArray();

        actionNames.Should().BeEquivalentTo([
            nameof(UserAdministrationController.Create),
            nameof(UserAdministrationController.List),
            nameof(UserAdministrationController.GetById),
            nameof(UserAdministrationController.Update),
            nameof(UserAdministrationController.AssignRole),
            nameof(UserAdministrationController.RemoveRole),
            nameof(UserAdministrationController.Block),
            nameof(UserAdministrationController.Unblock),
            nameof(UserAdministrationController.ResetPassword),
        ], "every administrative action must be explicitly approved before it exists here");
    }

    [Fact]
    public void ResetPasswordRequest_declares_exactly_newPassword()
    {
        // Incremento 3, Checkpoint 9, Section 1: "nenhum request pode aceitar
        // tenantId, actorId, email, passwordHash, securityStamp, motivo livre
        // ou identidade do executor" — the actor/tenant come exclusively from
        // AuthenticatedIdentityReader, and no current password is required
        // for an administrative reset.
        var requestBodyProperties = typeof(ResetPasswordRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);

        requestBodyProperties.Should().BeEquivalentTo(
            [nameof(ResetPasswordRequest.NewPassword)],
            $"{nameof(ResetPasswordRequest)} must declare exactly newPassword");
    }

    [Fact]
    public void No_ResetPassword_parameter_is_named_or_bound_as_a_tenant_or_actor_identifier()
    {
        string[] forbiddenFragments = ["tenantid", "actorid", "currentpassword"];
        var action = typeof(UserAdministrationController).GetMethod(nameof(UserAdministrationController.ResetPassword))!;

        foreach (var parameter in action.GetParameters())
        {
            forbiddenFragments.Should().NotContain(
                fragment => (parameter.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase),
                $"{action.Name}'s parameter '{parameter.Name}' must never bind a tenant/actor identifier or a current password from the route/query/body");
        }
    }

    [Fact]
    public void No_Update_parameter_is_named_or_bound_as_a_tenant_or_actor_identifier()
    {
        // Incremento 3, Checkpoint 8: "não aceitar tenantId, actorId,
        // updatedBy, roles, status, password ou normalizedEmail no body" —
        // the actor must come exclusively from AuthenticatedIdentityReader.
        string[] forbiddenFragments =
        [
            "tenantid", "actorid", "updatedby", "roles", "status", "password", "normalizedemail", "securitystamp",
        ];
        var updateAction = typeof(UserAdministrationController).GetMethod(nameof(UserAdministrationController.Update))!;

        foreach (var parameter in updateAction.GetParameters())
        {
            forbiddenFragments.Should().NotContain(
                fragment => (parameter.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase),
                $"{updateAction.Name}'s parameter '{parameter.Name}' must never bind a tenant/actor/role/status/password identifier from the route/query/body");
        }

        var requestBodyProperties = typeof(UpdateUserRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToList();
        requestBodyProperties.Select(p => p.Name).Should().BeEquivalentTo(
            [nameof(UpdateUserRequest.FullName), nameof(UpdateUserRequest.Email)],
            $"{nameof(UpdateUserRequest)} must declare exactly fullName/email — Section 1: \"DTO de request contém somente fullName e email\"");
    }

    [Fact]
    public void No_AssignRole_or_RemoveRole_parameter_is_named_or_bound_as_a_tenant_or_actor_identifier()
    {
        // Incremento 3, Checkpoint 6, Section 1: "não aceitar no body ou rota:
        // tenantId; actorId; assignedBy; removedBy" — the actor must come
        // exclusively from AuthenticatedIdentityReader.
        string[] forbiddenFragments = ["tenantid", "actorid", "assignedby", "removedby"];
        MethodInfo[] roleActions =
        [
            typeof(UserAdministrationController).GetMethod(nameof(UserAdministrationController.AssignRole))!,
            typeof(UserAdministrationController).GetMethod(nameof(UserAdministrationController.RemoveRole))!,
        ];

        foreach (var action in roleActions)
        {
            foreach (var parameter in action.GetParameters())
            {
                forbiddenFragments.Should().NotContain(
                    fragment => (parameter.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"{action.Name}'s parameter '{parameter.Name}' must never bind a tenant/actor identifier from the route/query/body");
            }
        }

        var requestBodyProperties = typeof(AssignRoleRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);
        requestBodyProperties.Should().NotContain(
            name => forbiddenFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
            $"{nameof(AssignRoleRequest)} must never declare a tenant/actor identifier field");
    }

    [Fact]
    public void Block_and_Unblock_accept_no_request_body_and_no_tenant_or_actor_parameter()
    {
        // Incremento 3, Checkpoint 7, Section 1: "não aceitar tenantId,
        // actorId, blockedBy ou unblockedBy no body ou na rota". Block/Unblock
        // go further than AssignRole/RemoveRole: they declare no [FromBody]
        // parameter at all, so there is structurally nowhere for such a field
        // to be bound from — checked here rather than relying on an HTTP-level
        // spot check.
        string[] forbiddenFragments = ["tenantid", "actorid", "blockedby", "unblockedby"];
        MethodInfo[] blockActions =
        [
            typeof(UserAdministrationController).GetMethod(nameof(UserAdministrationController.Block))!,
            typeof(UserAdministrationController).GetMethod(nameof(UserAdministrationController.Unblock))!,
        ];

        foreach (var action in blockActions)
        {
            var parameters = action.GetParameters();
            parameters.Should().NotContain(
                p => p.GetCustomAttribute<FromBodyAttribute>() != null,
                $"{action.Name} must accept no request body at all");

            foreach (var parameter in parameters)
            {
                forbiddenFragments.Should().NotContain(
                    fragment => (parameter.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"{action.Name}'s parameter '{parameter.Name}' must never bind a tenant/actor identifier from the route/query/body");
            }
        }
    }

    [Fact]
    public void Every_action_requires_exactly_the_USERS_MANAGE_policy()
    {
        foreach (var action in Actions())
        {
            var authorizeAttributes = action.GetCustomAttributes<AuthorizeAttribute>().ToArray();

            authorizeAttributes.Should().ContainSingle(
                a => a.Policy == IdentityPermissionCodes.UsersManage,
                $"{action.Name} must be protected by exactly the \"{IdentityPermissionCodes.UsersManage}\" policy, " +
                "never left open, bound to a different policy, or reachable by plain authentication alone");
            action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty(
                $"{action.Name} must never be reachable anonymously");
        }
    }

    [Fact]
    public void UserResponse_carries_no_property_whose_name_suggests_sensitive_or_internal_data()
    {
        // Section 6: never return password hash, normalized e-mail, security
        // stamp, tenantId, auth data or tokens — checked structurally so it
        // holds for every possible value, not just the ones exercised by
        // UserAdministrationEndpointsTests' HTTP-level spot checks.
        string[] forbiddenNameFragments =
        [
            "password", "normalizedemail", "securitystamp", "tenantid",
            "accesstoken", "refreshtoken", "tokenhash", "secret", "jwt",
        ];

        var offendingProperties = typeof(UserResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => forbiddenNameFragments.Any(fragment =>
                p.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        offendingProperties.Should().BeEmpty(
            $"{nameof(UserResponse)} (also carried inside {nameof(PagedUserResponse)}) must never expose a " +
            "password hash, normalized e-mail, security stamp, tenantId, or auth/token data (Documento, Section 6)");
    }
}
