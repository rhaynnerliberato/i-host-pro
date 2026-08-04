using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Http;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.PropertyManagement.Api.Controllers;

/// <summary>
/// Self-service listing/detail of the authenticated owner's own properties
/// (Checkpoint 5 plan, item 10) — a separate controller from
/// <see cref="PropertiesController"/>, mirroring Identity's own
/// administrative-vs-self split (<c>UserAdministrationController</c> vs.
/// <c>UsersController</c>): a different actor perspective
/// (<see cref="IdentityPermissionCodes.PropertiesReadOwnOwner"/>, never
/// <see cref="IdentityPermissionCodes.PropertiesManage"/>) and a different
/// route family (<c>/api/v1/properties/mine</c>, never accepting an
/// <c>ownerUserId</c> from the client — the id comes exclusively from
/// <see cref="PropertyManagementIdentityReader"/>). The ABAC filter itself
/// lives in the Application/Query layer (<see cref="IPropertyReader.ListMineAsync"/>/
/// <see cref="IPropertyReader.GetMineDetailAsync"/>), never only here.
/// </summary>
[ApiController]
[Route("api/v1/properties/mine")]
public sealed class MyPropertiesController : ControllerBase
{
    private readonly IPropertyManagementRequestDispatcher _sender;

    public MyPropertiesController(IPropertyManagementRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesReadOwnOwner)]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new ListMyPropertiesQuery(identity.UserId, page, pageSize), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{propertyId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesReadOwnOwner)]
    public async Task<IActionResult> GetById(Guid propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetMyPropertyDetailQuery(identity.UserId, propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    private static PropertyDetailResponse ToResponse(PropertyResult result) => new(
        result.Id,
        result.Code,
        result.Name,
        result.Capacity,
        result.CondominiumId,
        result.Address is null ? null : ToAddressResponse(result.Address),
        ToAddressResponse(result.EffectiveAddress),
        result.EffectiveAddressSource,
        result.Status,
        result.CreatedAt,
        result.UpdatedAt);

    private static AddressResponse ToAddressResponse(AddressResult address) => new(
        address.ZipCode, address.Street, address.Number, address.Complement,
        address.Neighborhood, address.City, address.State, address.Country);

    private static PagedPropertyResponse ToResponse(PagedResult<PropertySummaryResult> result) => new(
        result.Page, result.PageSize, result.TotalCount, result.Items.Select(ToResponse).ToArray());

    private static PropertySummaryResponse ToResponse(PropertySummaryResult result) => new(
        result.Id, result.Code, result.Name, result.Capacity, result.CondominiumId, result.Status,
        result.CreatedAt, result.UpdatedAt);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
