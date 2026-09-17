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
/// Web OAuth architecture gate — a SEPARATE controller from
/// <see cref="AirbnbEmailBridgeController"/> specifically so that
/// controller's own invariant ("every action requires
/// <see cref="IdentityPermissionCodes.IntegrationsManage"/>") stays literally
/// true: <see cref="Callback"/> is necessarily <see cref="AllowAnonymousAttribute"/>
/// from the iHostPro JWT scheme's point of view (Microsoft's redirect is a
/// plain top-level browser navigation, never carrying our
/// <c>Authorization: Bearer</c> header) — its entire security instead rests
/// on the single-use, hashed, short-lived OAuth transaction state (see
/// <see cref="IAirbnbEmailWebOAuthCallbackProcessor"/>'s own remarks).
/// </summary>
[ApiController]
[Route("api/v1/integrations/airbnb-email/oauth")]
public sealed class AirbnbEmailWebOAuthController : ControllerBase
{
    private readonly IExternalIntegrationsRequestDispatcher _sender;
    private readonly IAirbnbEmailWebOAuthCallbackProcessor _callbackProcessor;

    public AirbnbEmailWebOAuthController(
        IExternalIntegrationsRequestDispatcher sender, IAirbnbEmailWebOAuthCallbackProcessor callbackProcessor)
    {
        _sender = sender;
        _callbackProcessor = callbackProcessor;
    }

    [HttpPost("start")]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbEmailWebOAuthStartResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new StartAirbnbEmailWebOAuthCommand(identity.TenantId, identity.UserId), cancellationToken);

        return result.IsSuccess
            ? Ok(new AirbnbEmailWebOAuthStartResponse(result.Value.AuthorizationUrl))
            : ExternalIntegrationsResultHttpMapper.ToActionResult(result.Error);
    }

    /// <summary>
    /// Deliberately anonymous — see this class's own remarks. Always ends in
    /// a 302 redirect to the frontend, never a JSON error body: this request
    /// comes from a full-page browser navigation initiated by Microsoft, not
    /// from Angular's own HTTP client.
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        [FromQuery] string? state, [FromQuery] string? code, [FromQuery(Name = "error")] string? error, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var report = await _callbackProcessor.ProcessAsync(state, code, error, cancellationToken);

        if (report.Result == AirbnbEmailWebOAuthCallbackResult.NotConfigured)
            return NotFound();

        var resultCode = report.Result switch
        {
            AirbnbEmailWebOAuthCallbackResult.Success => "success",
            AirbnbEmailWebOAuthCallbackResult.UserCancelledOrConsentDenied => "denied",
            AirbnbEmailWebOAuthCallbackResult.InvalidOrExpiredState => "expired",
            _ => "error",
        };

        // Only ever a bounded, non-sensitive result code - never the
        // authorization code, state, tenant/user id, or any Microsoft error
        // text (Web OAuth architecture gate, item 21).
        var separator = report.FrontendReturnUrl!.Contains('?') ? '&' : '?';
        return Redirect($"{report.FrontendReturnUrl}{separator}connect={resultCode}");
    }
}
