using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Http;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.PropertyManagement.Api.Controllers;

/// <summary>
/// Administrative property creation/update/listing/detail (Checkpoint 3
/// plan) plus lifecycle transitions (Checkpoint 4 plan) — mirrors
/// <c>CondominiumsController</c> exactly. Every action requires
/// <see cref="IdentityPermissionCodes.PropertiesManage"/> (same
/// centrally-registered policy, no duplicate registration here). Every
/// action reads the actor exclusively from <see cref="PropertyManagementIdentityReader"/>
/// — never a request-body-supplied tenant/actor/status/owner id — and only
/// builds a Query/Command before dispatching through <see cref="IPropertyManagementRequestDispatcher"/>.
/// The three lifecycle actions accept no body at all — the controller never
/// touches Infrastructure, never consults the Condominium directly, never
/// creates auditoria or publishes an event itself.
///
/// Checkpoint 5 adds the three ADMINISTRATIVE Ownership actions
/// (link/unlink/list owners) — same <see cref="IdentityPermissionCodes.PropertiesManage"/>
/// policy as the rest of this controller. The self-service <c>mine</c>
/// endpoints live in a separate controller (<c>MyPropertiesController</c>),
/// mirroring Identity's own administrative-vs-self split
/// (<c>UserAdministrationController</c> vs. <c>UsersController</c>) — a
/// different actor perspective and policy (<see cref="IdentityPermissionCodes.PropertiesReadOwnOwner"/>).
/// No Group or Portaria endpoint exists in this controller or project
/// (Checkpoint 5 plan, restrictions).
/// </summary>
[ApiController]
[Route("api/v1/properties")]
public sealed class PropertiesController : ControllerBase
{
    private readonly IPropertyManagementRequestDispatcher _sender;

    public PropertiesController(IPropertyManagementRequestDispatcher sender) => _sender = sender;

    [HttpPost]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> Create([FromBody] CreatePropertyRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new CreatePropertyCommand(
            identity.TenantId,
            identity.UserId,
            request.Code ?? string.Empty,
            request.Name ?? string.Empty,
            request.Capacity ?? 0,
            request.CondominiumId,
            ToAddressInput(request.Address));

        var result = await _sender.Send(command, cancellationToken);

        if (!result.IsSuccess)
            return PropertyManagementResultHttpMapper.ToActionResult(result.Error);

        var response = ToResponse(result.Value);
        return CreatedAtAction(nameof(GetById), new { propertyId = response.Id }, response);
    }

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new ListPropertiesQuery(page, pageSize), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{propertyId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> GetById(Guid propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new GetPropertyDetailQuery(propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPatch("{propertyId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> Update(
        Guid propertyId, [FromBody] UpdatePropertyRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new UpdatePropertyCommand(
            identity.TenantId,
            identity.UserId,
            propertyId,
            request.Code,
            request.Name,
            request.Capacity,
            request.CondominiumId,
            ToAddressInputOptional(request.Address));

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{propertyId:guid}/activate")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> Activate(Guid propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new ActivatePropertyCommand(identity.TenantId, identity.UserId, propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{propertyId:guid}/deactivate")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> Deactivate(Guid propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new DeactivatePropertyCommand(identity.TenantId, identity.UserId, propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{propertyId:guid}/archive")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> Archive(Guid propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new ArchivePropertyCommand(identity.TenantId, identity.UserId, propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{propertyId:guid}/owners")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> LinkOwner(
        Guid propertyId, [FromBody] LinkPropertyOwnerRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new LinkPropertyOwnerCommand(identity.TenantId, identity.UserId, propertyId, request.OwnerUserId ?? Guid.Empty);

        var result = await _sender.Send(command, cancellationToken);

        if (!result.IsSuccess)
            return PropertyManagementResultHttpMapper.ToActionResult(result.Error);

        var response = ToResponse(result.Value);
        return CreatedAtAction(nameof(ListOwners), new { propertyId }, response);
    }

    [HttpDelete("{propertyId:guid}/owners/{ownerUserId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> UnlinkOwner(Guid propertyId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new UnlinkPropertyOwnerCommand(identity.TenantId, identity.UserId, propertyId, ownerUserId);

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? NoContent()
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{propertyId:guid}/owners")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    public async Task<IActionResult> ListOwners(
        Guid propertyId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new ListPropertyOwnersQuery(propertyId, page, pageSize), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    private static PropertyOwnerResponse ToResponse(PropertyOwnerResult result) => new(
        result.PropertyId, result.OwnerUserId, result.CreatedAt);

    private static PagedPropertyOwnerResponse ToResponse(PagedResult<PropertyOwnerResult> result) => new(
        result.Page, result.PageSize, result.TotalCount, result.Items.Select(ToResponse).ToArray());

    private static PropertyAddressInput? ToAddressInput(AddressRequest? request) => request is null
        ? null
        : new PropertyAddressInput(
            request.ZipCode ?? string.Empty,
            request.Street ?? string.Empty,
            request.Number ?? string.Empty,
            request.Complement,
            request.Neighborhood ?? string.Empty,
            request.City ?? string.Empty,
            request.State ?? string.Empty,
            request.Country ?? "BR");

    private static Optional<PropertyAddressInput?> ToAddressInputOptional(Optional<AddressRequest?> optional) =>
        optional.IsSet ? Optional<PropertyAddressInput?>.Of(ToAddressInput(optional.Value)) : Optional<PropertyAddressInput?>.Unset;

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
