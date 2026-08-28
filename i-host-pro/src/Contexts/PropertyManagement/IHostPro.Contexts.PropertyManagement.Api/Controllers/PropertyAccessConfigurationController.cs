using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Http;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.PropertyManagement.Api.Controllers;

/// <summary>
/// Administrative configuration of a Property's guest-access credential/
/// instructions (Fase 10, Checkpoint 6.2 — Guest Access Secure Delivery
/// Corrective Implementation). Reuses <see cref="IdentityPermissionCodes.PropertiesManage"/>
/// — the same policy every other Property-level mutation in this Bounded
/// Context already requires; no new permission was created for this
/// endpoint (CP6.2 mandate item 7). Never accepts or returns a raw
/// credential value — only <c>AccessCredentialSecretReference</c>, the
/// reference the administrator configures out-of-band.
/// </summary>
[ApiController]
[Route("api/v1/properties/{propertyId:guid}/access-configuration")]
public sealed class PropertyAccessConfigurationController : ControllerBase
{
    private readonly IPropertyManagementRequestDispatcher _sender;

    public PropertyAccessConfigurationController(IPropertyManagementRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    [ProducesResponseType(typeof(PropertyAccessConfigurationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new GetPropertyAccessConfigurationQuery(propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPut]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    [ProducesResponseType(typeof(PropertyAccessConfigurationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Set(
        Guid propertyId, [FromBody] SetPropertyAccessConfigurationRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new SetPropertyAccessConfigurationCommand(
            identity.TenantId, identity.UserId, propertyId,
            request.AccessCredentialSecretReference, request.AccessInstructions, request.IsActive);

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    private static PropertyAccessConfigurationResponse ToResponse(PropertyAccessConfigurationResult result) => new(
        result.Id, result.PropertyId, result.AccessCredentialSecretReference, result.AccessInstructions,
        result.IsActive, result.CreatedAtUtc, result.UpdatedAtUtc);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
