using IHostPro.Contexts.ExternalIntegrations.Api.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Api.Http;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Controllers;

/// <summary>
/// Administrative WhatsApp integration configuration endpoints (Fase 9,
/// Checkpoint 2.1 — "External Integrations + Credential/Configuration
/// Foundation") — mirrors <c>TemplatesController</c>'s structure exactly.
/// Both actions require <see cref="IdentityPermissionCodes.IntegrationsManage"/>
/// — no <c>INTEGRATIONS:READ</c> counterpart exists (CP2.1 mandate §24).
///
/// Never accepts or returns a secret value (CP2.1 mandate §25/§26): the
/// request/response only carry secret REFERENCES (opaque names) and
/// <c>*Configured</c> booleans — never the secret itself. No endpoint here
/// can activate real WhatsApp sending — <c>IsEnabled</c> is never settable
/// through this controller (CP2.1 mandate §18).
/// </summary>
[ApiController]
[Route("api/v1/integrations/whatsapp")]
public sealed class WhatsAppIntegrationController : ControllerBase
{
    private readonly IExternalIntegrationsRequestDispatcher _sender;

    public WhatsAppIntegrationController(IExternalIntegrationsRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(WhatsAppIntegrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetWhatsAppIntegrationQuery(identity.TenantId), cancellationToken);

        return Ok(ToResponse(result.Value));
    }

    [HttpPut]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(WhatsAppIntegrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Configure([FromBody] ConfigureWhatsAppIntegrationRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new ConfigureWhatsAppIntegrationCommand(
                identity.TenantId,
                request.WabaId,
                request.PhoneNumberId,
                request.AccessTokenSecretReference,
                request.AppSecretSecretReference,
                request.VerifyTokenSecretReference),
            cancellationToken);

        return Ok(ToResponse(result.Value));
    }

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";

    private static WhatsAppIntegrationResponse ToResponse(WhatsAppIntegrationResult result) => new(
        result.TenantId,
        result.WabaId,
        result.PhoneNumberId,
        result.IsEnabled,
        result.AccessTokenConfigured,
        result.AppSecretConfigured,
        result.VerifyTokenConfigured,
        result.CreatedAtUtc,
        result.UpdatedAtUtc);
}
