using IHostPro.Contexts.ExternalIntegrations.Api.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Api.Http;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Controllers;

/// <summary>
/// Administrative Airbnb Email Bridge listing-title mapping endpoints —
/// mirrors <c>AirbnbEmailBridgeController</c>'s structure, same
/// <see cref="IdentityPermissionCodes.IntegrationsManage"/> policy. Minimal
/// by design (Mapping + DRY_RUN Orchestration gate, Fase 9 review): create
/// and list only, no update/delete yet — none was requested, and this
/// mapping is meant to be a small, deliberate, rarely-changed set per tenant.
/// </summary>
[ApiController]
[Route("api/v1/integrations/airbnb-email/listing-title-mappings")]
public sealed class AirbnbListingTitleMappingController : ControllerBase
{
    private readonly IExternalIntegrationsRequestDispatcher _sender;

    public AirbnbListingTitleMappingController(IExternalIntegrationsRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(IReadOnlyList<AirbnbListingTitleMappingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new ListAirbnbListingTitleMappingsQuery(identity.TenantId), cancellationToken);

        return Ok(result.Value.Select(ToResponse).ToList());
    }

    [HttpPost]
    [Authorize(Policy = IdentityPermissionCodes.IntegrationsManage)]
    [ProducesResponseType(typeof(AirbnbListingTitleMappingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateAirbnbListingTitleMappingRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ExternalIntegrationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new CreateAirbnbListingTitleMappingCommand(identity.TenantId, request.ListingTitle, request.PropertyId),
            cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : ExternalIntegrationsResultHttpMapper.ToActionResult(result.Error);
    }

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";

    private static AirbnbListingTitleMappingResponse ToResponse(AirbnbListingTitleMappingResult result) => new(
        result.Id, result.TenantId, result.ListingTitle, result.PropertyId, result.CreatedAtUtc, result.UpdatedAtUtc);
}
