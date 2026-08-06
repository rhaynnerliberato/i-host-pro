using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Http;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.PropertyManagement.Api.Controllers;

/// <summary>
/// Administrative condominium creation/update/listing/detail (Checkpoint 2
/// plan). Every action requires <see cref="IdentityPermissionCodes.PropertiesManage"/>
/// — the same policy, registered centrally by Identity's own
/// <c>AddIdentityAuthorization</c> and resolved by its generic
/// <c>PermissionAuthorizationHandler</c> (Checkpoint 1/2 approved design; no
/// duplicate policy registered here). Every action reads the actor
/// exclusively from <see cref="PropertyManagementIdentityReader"/> — never a
/// request-body-supplied actor/tenant id — and only builds a Query/Command
/// before dispatching through <see cref="IPropertyManagementRequestDispatcher"/>;
/// never touches Infrastructure, never contains a domain rule, never
/// assembles auditing or publishes an event itself. No Imóveis (Property)
/// endpoint exists in this controller or project (Checkpoint 2 plan,
/// restrictions).
/// </summary>
[ApiController]
[Route("api/v1/condominiums")]
public sealed class CondominiumsController : ControllerBase
{
    private readonly IPropertyManagementRequestDispatcher _sender;

    public CondominiumsController(IPropertyManagementRequestDispatcher sender) => _sender = sender;

    [HttpPost]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    [ProducesResponseType(typeof(CondominiumDetailResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateCondominiumRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new CreateCondominiumCommand(
            identity.TenantId,
            identity.UserId,
            request.Name ?? string.Empty,
            ToAddressInput(request.Address));

        var result = await _sender.Send(command, cancellationToken);

        if (!result.IsSuccess)
            return PropertyManagementResultHttpMapper.ToActionResult(result.Error);

        var response = ToResponse(result.Value);
        return CreatedAtAction(nameof(GetById), new { condominiumId = response.Id }, response);
    }

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    [ProducesResponseType(typeof(PagedCondominiumResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new ListCondominiumsQuery(page, pageSize), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{condominiumId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    [ProducesResponseType(typeof(CondominiumDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid condominiumId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new GetCondominiumDetailQuery(condominiumId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPatch("{condominiumId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.PropertiesManage)]
    [ProducesResponseType(typeof(CondominiumDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid condominiumId, [FromBody] UpdateCondominiumRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!PropertyManagementIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new UpdateCondominiumCommand(
            identity.TenantId, identity.UserId, condominiumId, request.Name, ToAddressInput(request.Address));

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PropertyManagementResultHttpMapper.ToActionResult(result.Error);
    }

    private static CondominiumAddressInput? ToAddressInput(AddressRequest? request) => request is null
        ? null
        : new CondominiumAddressInput(
            request.ZipCode ?? string.Empty,
            request.Street ?? string.Empty,
            request.Number ?? string.Empty,
            request.Complement,
            request.Neighborhood ?? string.Empty,
            request.City ?? string.Empty,
            request.State ?? string.Empty,
            request.Country ?? "BR");

    private static CondominiumDetailResponse ToResponse(CondominiumResult result) => new(
        result.Id,
        result.Name,
        new AddressResponse(
            result.Address.ZipCode, result.Address.Street, result.Address.Number, result.Address.Complement,
            result.Address.Neighborhood, result.Address.City, result.Address.State, result.Address.Country),
        result.CreatedAt,
        result.UpdatedAt);

    private static PagedCondominiumResponse ToResponse(PagedResult<CondominiumSummaryResult> result) => new(
        result.Page, result.PageSize, result.TotalCount, result.Items.Select(ToResponse).ToArray());

    private static CondominiumSummaryResponse ToResponse(CondominiumSummaryResult result) => new(
        result.Id, result.Name, result.CreatedAt, result.UpdatedAt);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
