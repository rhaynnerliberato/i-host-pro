using System.Security.Claims;
using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Api.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Api.Controllers;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Api;

/// <summary>
/// Fase 9, Checkpoint 2.1.1 mandate §20: proves the authenticated user id
/// read from claims actually reaches <see cref="ConfigureWhatsAppIntegrationCommand.ActorUserId"/>
/// — the one piece of plumbing the new audit behavior depends on. A direct
/// controller-level unit test (not a full HTTP/JWT integration test — no new
/// endpoint, no JWT signing/validation exercised here, mirroring the
/// project's own precedent of never unit-testing claim-reader plumbing via a
/// full token, e.g. <c>ConfigurationIdentityReader</c> has no dedicated test
/// of its own either).
/// </summary>
public class WhatsAppIntegrationControllerActorPropagationTests
{
    private sealed class RecordingDispatcher : IExternalIntegrationsRequestDispatcher
    {
        public object? LastRequest { get; private set; }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            if (request is ConfigureWhatsAppIntegrationCommand)
            {
                var result = Result.Success(new WhatsAppIntegrationResult(
                    Guid.Empty, null, null, false, false, false, false, DateTimeOffset.UtcNow, null));
                return new ValueTask<TResponse>((TResponse)(object)result);
            }

            throw new InvalidOperationException($"Unexpected request type: {request.GetType()}");
        }
    }

    [Fact]
    public async Task Configure_passes_the_authenticated_users_own_id_as_ActorUserId_never_a_client_supplied_value()
    {
        var tenantId = Guid.NewGuid();
        var authenticatedUserId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher();
        var controller = new WhatsAppIntegrationController(dispatcher)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("sub", authenticatedUserId.ToString("D")),
                        new Claim("tenant_id", tenantId.ToString("D")),
                    ])),
                },
            },
        };

        await controller.Configure(
            new ConfigureWhatsAppIntegrationRequest("waba-1", "phone-1", null, null, null),
            CancellationToken.None);

        dispatcher.LastRequest.Should().BeOfType<ConfigureWhatsAppIntegrationCommand>();
        var command = (ConfigureWhatsAppIntegrationCommand)dispatcher.LastRequest!;
        command.ActorUserId.Should().Be(authenticatedUserId,
            "the audit trail must record who actually authenticated the request, never a value the client could choose");
        command.TenantId.Should().Be(tenantId);
    }
}
