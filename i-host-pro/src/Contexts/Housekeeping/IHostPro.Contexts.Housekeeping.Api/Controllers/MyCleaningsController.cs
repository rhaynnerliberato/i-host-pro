using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Api.Contracts;
using IHostPro.Contexts.Housekeeping.Api.Http;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Checklist;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Occurrences;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Housekeeping.Api.Controllers;

/// <summary>
/// Self-service listing/detail of the caller's OWN cleanings (Fase 6,
/// Incremento 2A — Portal da Faxineira Core). Deliberately a separate
/// controller/route from <see cref="CleaningsController"/> (never an
/// admin-endpoint-with-optional-housekeeperUserId shortcut — approval §10):
/// every action here is gated by <see cref="IdentityPermissionCodes.CleaningsManageOwnCleaning"/>
/// and the caller's own <c>sub</c> claim is the ONLY source of the
/// housekeeper identity used to filter — a client-supplied
/// <c>housekeeperUserId</c>/filter is never accepted anywhere in this
/// controller. Both the "not found" and "found but not mine" cases resolve
/// to the same 404 (<see cref="ICleaningReader.GetByIdForHousekeeperAsync"/>
/// returns <c>null</c> for both), so a cross-tenant or wrong-housekeeper
/// lookup never reveals whether the cleaning exists (§4 ABAC rule).
/// </summary>
[ApiController]
[Route("api/v1/my-cleanings")]
public sealed class MyCleaningsController : ControllerBase
{
    private readonly IHousekeepingRequestDispatcher _sender;

    public MyCleaningsController(IHousekeepingRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(PagedCleaningResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new ListOwnCleaningsQuery(identity.UserId, status, page, pageSize), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{cleaningId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetOwnCleaningDetailQuery(cleaningId, identity.UserId), cancellationToken);

        return result.IsSuccess
            ? Ok(CleaningsController.ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/in-transit")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarkInTransit(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new MarkOwnCleaningInTransitCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(CleaningsController.ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/start")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new StartOwnCleaningCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(CleaningsController.ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/start-inspection")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartInspection(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new StartOwnCleaningInspectionCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(CleaningsController.ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/complete")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new CompleteOwnCleaningCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(CleaningsController.ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/waiting-materials")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> WaitingMaterials(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new MarkOwnCleaningWaitingMaterialsCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(CleaningsController.ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/waiting-help")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> WaitingHelp(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new MarkOwnCleaningWaitingHelpCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(CleaningsController.ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/delay")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReportDelay(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new ReportOwnCleaningDelayCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(CleaningsController.ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/occurrences")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningOccurrenceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterOccurrence(
        Guid cleaningId, [FromBody] RegisterCleaningOccurrenceRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new RegisterCleaningOccurrenceCommand(
            identity.TenantId, identity.UserId, cleaningId, request.Type ?? string.Empty, request.Description);
        var result = await _sender.Send(command, cancellationToken);

        if (!result.IsSuccess)
            return HousekeepingResultHttpMapper.ToActionResult(result.Error);

        var response = ToResponse(result.Value);
        return CreatedAtAction(nameof(ListOccurrences), new { cleaningId }, response);
    }

    [HttpGet("{cleaningId:guid}/occurrences")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(IReadOnlyCollection<CleaningOccurrenceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListOccurrences(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new ListCleaningOccurrencesQuery(cleaningId, identity.UserId), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value.Select(ToResponse).ToArray())
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{cleaningId:guid}/checklist")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(IReadOnlyCollection<CleaningChecklistItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetChecklist(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetOwnCleaningChecklistQuery(cleaningId, identity.UserId), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value.Select(ToResponse).ToArray())
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPut("{cleaningId:guid}/checklist/{itemType}")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManageOwnCleaning)]
    [ProducesResponseType(typeof(CleaningChecklistItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetChecklistItem(
        Guid cleaningId, string itemType, [FromBody] SetChecklistItemRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new SetOwnCleaningChecklistItemCommand(
            identity.TenantId, identity.UserId, cleaningId, itemType, request.IsChecked);
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    private static CleaningChecklistItemResponse ToResponse(CleaningChecklistItemResult result) => new(
        result.CleaningId, result.ItemType, result.IsChecked, result.UpdatedByUserId, result.UpdatedAtUtc);

    private static CleaningOccurrenceResponse ToResponse(CleaningOccurrenceResult result) => new(
        result.Id, result.CleaningId, result.Type, result.Description, result.RegisteredByUserId, result.RegisteredAtUtc);

    private static PagedCleaningResponse ToResponse(PagedResult<CleaningSummaryResult> result) => new(
        result.Page, result.PageSize, result.TotalCount, result.Items.Select(CleaningsController.ToResponse).ToArray());

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
