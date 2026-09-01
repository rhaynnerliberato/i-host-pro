using IHostPro.Api.Http;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Api.Controllers;

/// <summary>
/// Manages the Tenant's single administrator notification contact (Fase 11,
/// Checkpoint 6 — the real recipient for
/// <see cref="SendHumanHandoffNotificationCommand"/>). Communication owns
/// this entirely — this controller never sees/returns the destination phone
/// to anywhere outside this Tenant's own authorized administrator; it is
/// never resolved by/returned to the AI Agent Bounded Context. Guarded by
/// <see cref="IdentityPermissionCodes.AiAgentManage"/>, reusing
/// <see cref="AIAgentIdentityReader"/> — the actor/tenant come exclusively
/// from the authenticated principal, never the request body.
/// </summary>
[ApiController]
[Route("api/v1/ai-agent/administrator-notification-contact")]
public sealed class AdministratorNotificationContactsController : ControllerBase
{
    private readonly ICommunicationRequestDispatcher _sender;

    public AdministratorNotificationContactsController(ICommunicationRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.AiAgentManage)]
    [ProducesResponseType(typeof(AdministratorNotificationContactResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!AIAgentIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetAdministratorNotificationContactQuery { TenantId = identity.TenantId }, cancellationToken);

        if (result.IsFailure)
            return Conflict();

        return result.Value is null ? NotFound() : Ok(result.Value);
    }

    [HttpPut]
    [Authorize(Policy = IdentityPermissionCodes.AiAgentManage)]
    [ProducesResponseType(typeof(AdministratorNotificationContactResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Upsert(UpsertAdministratorNotificationContactHttpRequest body, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!AIAgentIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new UpsertAdministratorNotificationContactCommand
        {
            TenantId = identity.TenantId,
            DestinationPhone = body.DestinationPhone,
        };
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : BadRequest();
    }
}

public sealed record UpsertAdministratorNotificationContactHttpRequest(string DestinationPhone);
