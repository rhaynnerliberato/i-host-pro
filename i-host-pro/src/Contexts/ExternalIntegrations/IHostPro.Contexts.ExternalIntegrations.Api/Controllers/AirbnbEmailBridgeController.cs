using IHostPro.Contexts.ExternalIntegrations.Api.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Api.Http;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Controllers;

/// <summary>
/// Administrative Airbnb Email Bridge connection endpoints — mirrors
/// <c>WhatsAppIntegrationController</c>'s structure, same
/// <see cref="IdentityPermissionCodes.IntegrationsManage"/> policy. Minimal
/// by design (Fase 9 review §20): only Connect/Disconnect exist, no
/// speculative full configuration UI yet.
///
/// <see cref="Connect"/> runs an interactive Microsoft sign-in with a system
/// browser on the machine hosting this Api process — it blocks until the
/// caller completes (or cancels) that sign-in, so it is intended to be
/// called against a local development Api instance only (Fase 9 review §21).
/// </summary>
[ApiController]
[Route("api/v1/integrations/airbnb-email")]
public sealed class AirbnbEmailBridgeController : ControllerBase
{
    private readonly IExternalIntegrationsRequestDispatcher _sender;

    public AirbnbEmailBridgeController(IExternalIntegrationsRequestDispatcher sender) => _sender = sender;

    [HttpPost("connect")]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbEmailBridgeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new ConnectAirbnbEmailMailboxCommand(identity.TenantId, identity.UserId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : ExternalIntegrationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("disconnect")]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbEmailBridgeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new DisconnectAirbnbEmailMailboxCommand(identity.TenantId, identity.UserId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : ExternalIntegrationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("enable-auto-publication")]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbAutoPublicationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EnableAutoPublication([FromBody] EnableAirbnbAutoPublicationRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new EnableAirbnbAutoPublicationCommand(identity.TenantId, identity.UserId, request.NotBeforeUtc), cancellationToken);

        return result.IsSuccess
            ? Ok(ToStatusResponse(result.Value))
            : ExternalIntegrationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("disable-auto-publication")]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbAutoPublicationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DisableAutoPublication(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new DisableAirbnbAutoPublicationCommand(identity.TenantId, identity.UserId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToStatusResponse(result.Value))
            : ExternalIntegrationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("auto-publication")]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbAutoPublicationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAutoPublicationStatus(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetAirbnbAutoPublicationStatusQuery(identity.TenantId), cancellationToken);

        return Ok(ToStatusResponse(result.Value));
    }

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";

    private static AirbnbEmailBridgeResponse ToResponse(AirbnbEmailMailboxConnectionResult result) => new(
        result.TenantId,
        result.MailboxAddress,
        result.AuthorizationStatus,
        result.IsEnabled,
        result.LastAuthenticatedAtUtc,
        result.CreatedAtUtc,
        result.UpdatedAtUtc);

    private static AirbnbAutoPublicationStatusResponse ToStatusResponse(AirbnbAutoPublicationStatusResult result) => new(
        result.TenantId, result.AutoPublishEnabled, result.AutoPublishNotBeforeUtc);
}
