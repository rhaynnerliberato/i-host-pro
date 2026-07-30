using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards the four self-service endpoints (Incremento 3, Checkpoints 4 and
/// 9): every action must require authentication, none may accept a
/// client-supplied user or tenant identifier (route, query or body), and no
/// action beyond the four approved ones may exist — there is no endpoint to
/// list or revoke another user's sessions, and no self-service password
/// recovery flow. Plain reflection, not NetArchTest: these are per-action
/// attribute/parameter checks, not assembly-level dependency rules (see
/// <c>IdentityDependencyTests</c> for those).
/// </summary>
public class IdentityUsersEndpointsArchitectureTests
{
    private static MethodInfo[] Actions() =>
        typeof(UsersController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();

    [Fact]
    public void Controller_exposes_exactly_the_four_approved_self_service_actions()
    {
        var actionNames = Actions().Select(m => m.Name).ToArray();

        actionNames.Should().BeEquivalentTo([
            nameof(UsersController.GetOwnProfile),
            nameof(UsersController.ListOwnSessions),
            nameof(UsersController.RevokeOwnSession),
            nameof(UsersController.ChangeOwnPassword),
        ], "no endpoint may exist here to list or act on another user's resources");
    }

    [Fact]
    public void ChangePasswordRequest_declares_exactly_currentPassword_and_newPassword()
    {
        // Incremento 3, Checkpoint 9, Section 1: "nenhum request pode aceitar
        // tenantId, actorId, email, passwordHash, securityStamp, motivo livre
        // ou identidade do executor" — the actor/tenant come exclusively from
        // AuthenticatedIdentityReader.
        var requestBodyProperties = typeof(ChangePasswordRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);

        requestBodyProperties.Should().BeEquivalentTo(
            [nameof(ChangePasswordRequest.CurrentPassword), nameof(ChangePasswordRequest.NewPassword)],
            $"{nameof(ChangePasswordRequest)} must declare exactly currentPassword/newPassword");
    }

    [Fact]
    public void Every_action_requires_authentication()
    {
        foreach (var action in Actions())
        {
            action.GetCustomAttributes<AuthorizeAttribute>().Should().ContainSingle(
                $"{action.Name} must require an authenticated caller — every action here operates on the caller's own resource");
            action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty(
                $"{action.Name} must never be reachable anonymously");
        }
    }

    [Fact]
    public void No_action_route_template_contains_a_user_or_tenant_identifier_segment()
    {
        var controllerRoute = typeof(UsersController).GetCustomAttribute<RouteAttribute>()!.Template;
        controllerRoute.Should().NotContainEquivalentOf("userId");
        controllerRoute.Should().NotContainEquivalentOf("tenantId");

        foreach (var action in Actions())
        {
            var routeTemplates = action.GetCustomAttributes()
                .OfType<Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider>()
                .Select(a => a.Template ?? string.Empty);

            foreach (var template in routeTemplates)
            {
                template.Should().NotContainEquivalentOf("userId", $"{action.Name}'s route must never accept a client-supplied user id");
                template.Should().NotContainEquivalentOf("tenantId", $"{action.Name}'s route must never accept a client-supplied tenant id");
            }
        }
    }

    [Fact]
    public void No_action_parameter_is_named_or_bound_as_a_user_or_tenant_identifier()
    {
        foreach (var action in Actions())
        {
            var parameterNames = action.GetParameters().Select(p => p.Name ?? string.Empty);

            parameterNames.Should().NotContain(
                name => name.Contains("userId", StringComparison.OrdinalIgnoreCase),
                $"{action.Name} must never bind a userId from the route/query/body");
            parameterNames.Should().NotContain(
                name => name.Contains("tenantId", StringComparison.OrdinalIgnoreCase),
                $"{action.Name} must never bind a tenantId from the route/query/body");
        }
    }

    [Fact]
    public void RevokeOwnSession_accepts_only_a_sessionId_route_parameter_besides_the_cancellation_token()
    {
        var action = typeof(UsersController).GetMethod(nameof(UsersController.RevokeOwnSession))!;

        var parameters = action.GetParameters();
        parameters.Should().HaveCount(2);
        parameters.Should().ContainSingle(p => p.ParameterType == typeof(Guid) && p.Name == "sessionId");
        parameters.Should().ContainSingle(p => p.ParameterType == typeof(CancellationToken));
    }
}
