using IHostPro.Contexts.ExternalIntegrations.Api.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Api.Http;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Controllers;

/// <summary>
/// Administrative WhatsApp template mapping endpoints (Fase 9, Checkpoint
/// 2.2 — mandate §50/§51) — mirrors <c>WhatsAppIntegrationController</c>'s
/// structure exactly, reusing the same <see cref="IdentityPermissionCodes.IntegrationsManage"/>
/// permission (no new permission, no dedicated UI this checkpoint). Never
/// exposes a secret — a template mapping carries none.
/// </summary>
[ApiController]
[Route("api/v1/integrations/whatsapp/template-mappings")]
public sealed class WhatsAppTemplateMappingController : ControllerBase
{
    private readonly IExternalIntegrationsRequestDispatcher _sender;

    public WhatsAppTemplateMappingController(IExternalIntegrationsRequestDispatcher sender) => _sender = sender;

    [HttpGet("{templateKey}")]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(WhatsAppTemplateMappingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(string templateKey, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetWhatsAppTemplateMappingQuery(identity.TenantId, templateKey), cancellationToken);

        return Ok(ToResponse(result.Value));
    }

    [HttpPut]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(WhatsAppTemplateMappingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Configure([FromBody] ConfigureWhatsAppTemplateMappingRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new ConfigureWhatsAppTemplateMappingCommand(
                identity.TenantId,
                identity.UserId,
                request.TemplateKey,
                request.ProviderTemplateName,
                request.LanguageCode,
                request.ParameterOrder),
            cancellationToken);

        return Ok(ToResponse(result.Value));
    }

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";

    private static WhatsAppTemplateMappingResponse ToResponse(WhatsAppTemplateMappingResult result) => new(
        result.TenantId,
        result.TemplateKey,
        result.ProviderTemplateName,
        result.LanguageCode,
        result.ParameterOrder,
        result.CreatedAtUtc,
        result.UpdatedAtUtc);
}
