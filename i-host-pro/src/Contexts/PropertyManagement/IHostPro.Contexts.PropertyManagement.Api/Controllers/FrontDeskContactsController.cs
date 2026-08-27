using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Http;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.FrontDesk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.PropertyManagement.Api.Controllers;

/// <summary>
/// Administrative configuration of a Condominium's front desk ("Portaria")
/// contact (Fase 10, Checkpoint 4 — Portaria Notification Foundation).
/// Reuses <see cref="IdentityPermissionCodes.PropertiesManage"/> — the same
/// policy every other Condominium-level mutation in this Bounded Context
/// already requires; no new permission was created for this endpoint
/// (user-decided). Portaria itself is never an authenticated actor of this
/// system — it is an external, passive operational recipient (Fase 10,
/// Checkpoint 4 mandate) — only an Administrator configures its contact
/// here.
/// </summary>
[ApiController]
[Route("api/v1/condominiums/{condominiumId:guid}/front-desk-contact")]
public sealed class FrontDeskContactsController : ControllerBase
{
    private readonly IPropertyManagementRequestDispatcher _sender;

    public FrontDeskContactsController(IPropertyManagementRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    [ProducesResponseType(typeof(FrontDeskContactResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid condominiumId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new GetFrontDeskContactByCondominiumQuery(condominiumId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPut]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    [ProducesResponseType(typeof(FrontDeskContactResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Set(
        Guid condominiumId, [FromBody] SetFrontDeskContactRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new SetFrontDeskContactCommand(
            identity.TenantId, identity.UserId, condominiumId,
            request.DisplayName ?? string.Empty, request.PhoneNumber ?? string.Empty, request.IsActive);

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    private static FrontDeskContactResponse ToResponse(FrontDeskContactResult result) => new(
        result.Id, result.CondominiumId, result.DisplayName, result.PhoneNumber,
        result.IsActive, result.CreatedAtUtc, result.UpdatedAtUtc);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
