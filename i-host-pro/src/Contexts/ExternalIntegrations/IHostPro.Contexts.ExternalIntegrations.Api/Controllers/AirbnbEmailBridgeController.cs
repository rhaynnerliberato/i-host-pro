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
/// <see cref="IdentityPermissionCodes.IntegrationsManage"/> policy.
///
/// <see cref="Connect"/> runs an interactive Microsoft sign-in with a system
/// browser on the machine hosting this Api process — it blocks until the
/// caller completes (or cancels) that sign-in, so it remains an
/// operational/engineering-only endpoint, called against a local development
/// Api instance directly (Fase 9 review §21). It is never called from the
/// product UI, which instead drives the redirect-based Web OAuth flow
/// (<see cref="AirbnbEmailWebOAuthController"/>, <c>oauth/start</c>/
/// <c>oauth/callback</c>) added by the Web OAuth Multi-Tenant Connect gate —
/// the self-service path a real deployed multi-tenant frontend can actually
/// drive, since it never opens a browser on the Api's own host.
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

    /// <summary>
    /// Minimal Operations/UX gate — the mailbox connection status read model.
    /// Always 200, never 404: a tenant that has never connected a mailbox
    /// gets <see cref="AirbnbEmailConnectionStatus.NotConfigured"/>, not an
    /// error. This is the ONLY connection-status source the frontend uses -
    /// it must never infer status from a previous Connect/Disconnect
    /// response.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbEmailBridgeStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetAirbnbEmailBridgeStatusQuery(identity.TenantId), cancellationToken);

        return Ok(ToStatusResponse(result.Value));
    }

    /// <summary>Minimal Operations/UX gate — tenant-scoped receipt processing counts only, never individual receipts (deliberately deferred).</summary>
    [HttpGet("processing-summary")]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbEmailProcessingSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProcessingSummary(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetAirbnbEmailProcessingSummaryQuery(identity.TenantId), cancellationToken);

        return Ok(ToProcessingSummaryResponse(result.Value));
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

    private static AirbnbEmailBridgeStatusResponse ToStatusResponse(AirbnbEmailBridgeStatusResult result) => new(
        result.TenantId, result.Status, result.IsEnabled, result.LastAuthenticatedAtUtc, result.MailboxAddress);

    private static AirbnbEmailProcessingSummaryResponse ToProcessingSummaryResponse(AirbnbEmailProcessingSummaryResult result) => new(
        result.TenantId, result.Pending, result.Processed, result.NeedsReview, result.Failed, result.Ignored);
}
