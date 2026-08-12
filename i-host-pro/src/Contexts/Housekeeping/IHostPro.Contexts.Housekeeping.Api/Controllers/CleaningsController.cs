using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Api.Contracts;
using IHostPro.Contexts.Housekeeping.Api.Http;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Housekeeping.Api.Controllers;

/// <summary>
/// Administrative cleaning creation/assignment/lifecycle/listing/detail
/// (Fase 6, Incremento 1) — mirrors <c>ReservationsController</c> exactly.
/// Every action requires <see cref="IdentityPermissionCodes.CleaningsManage"/>
/// (ADMIN/OPERATOR only this increment — <c>CLEANINGS:MANAGE:OWN_CLEANING</c>,
/// reserved for the Housekeeper role, is not exercised by this controller;
/// it belongs to the Portal da Faxineira, Incremento 2). Every action reads
/// the actor exclusively from <see cref="HousekeepingIdentityReader"/> —
/// never a request-body-supplied tenant/actor id. No generic PATCH exists —
/// every lifecycle transition is its own explicit action/command (approval
/// §16).
/// </summary>
[ApiController]
[Route("api/v1/cleanings")]
public sealed class CleaningsController : ControllerBase
{
    private readonly IHousekeepingRequestDispatcher _sender;

    public CleaningsController(IHousekeepingRequestDispatcher sender) => _sender = sender;

    [HttpPost]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateCleaningRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new CreateCleaningCommand(
            identity.TenantId, identity.UserId, request.PropertyId ?? Guid.Empty, request.ReservationId,
            request.ScheduledAtUtc);

        var result = await _sender.Send(command, cancellationToken);

        if (!result.IsSuccess)
            return HousekeepingResultHttpMapper.ToActionResult(result.Error);

        var response = ToDetailResponse(result.Value);
        return CreatedAtAction(nameof(GetById), new { cleaningId = response.Id }, response);
    }

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
    [ProducesResponseType(typeof(PagedCleaningResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] Guid? propertyId, [FromQuery] Guid? assignedHousekeeperUserId,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(
            new ListCleaningsQuery(status, propertyId, assignedHousekeeperUserId, page, pageSize), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{cleaningId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new GetCleaningDetailQuery(cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/assign")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Assign(
        Guid cleaningId, [FromBody] AssignCleaningRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        if (request.HousekeeperUserId is not { } housekeeperUserId)
        {
            var problem = new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = "validation_error" };
            problem.Extensions["codes"] = new[] { "housekeeper_user_id_required" };
            return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
        }

        var command = new AssignCleaningCommand(identity.TenantId, identity.UserId, cleaningId, housekeeperUserId);
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/start")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new StartCleaningCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/start-inspection")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
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
            new StartCleaningInspectionCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/complete")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new CompleteCleaningCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/cancel")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    // Named CancelCleaning (not Cancel) as part of the OpenAPI operationId
    // stability gate (Fase 6, Checkpoint 6). Root cause of the original
    // regression: Swashbuckle leaves OperationId unset by default in this
    // project (no CustomOperationIds configured until this gate), so NSwag's
    // TypeScript generator synthesized its OWN operation name from the LAST
    // path segment when this action's C# method was still named "Cancel" —
    // entirely independent of the C# method name. Since
    // ReservationsController's cancel action also ends in ".../cancel",
    // NSwag produced two identical synthetic names ("cancel"/"cancel2"),
    // silently overwriting Reservations' generated client method to point at
    // this Cleanings route instead. The definitive fix is Program.cs's
    // AddSwaggerGen CustomOperationIds, which now assigns this action the
    // explicit, unique OperationId "CancelCleaning" (and
    // ReservationsController.CancelReservation gets "CancelReservation") —
    // the method rename here exists only so the operationId reads naturally
    // from the (now equally explicit) C# action name. The HTTP route itself
    // (POST /api/v1/cleanings/{cleaningId}/cancel) is unaffected.
    public async Task<IActionResult> CancelCleaning(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new CancelCleaningCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/interrupt")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
    [ProducesResponseType(typeof(CleaningDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Interrupt(Guid cleaningId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!HousekeepingIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new MarkCleaningInterruptedCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/waiting-materials")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
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
            new MarkCleaningWaitingMaterialsCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{cleaningId:guid}/waiting-help")]
    [Authorize(Policy = IdentityPermissionCodes.CleaningsManage)]
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
            new MarkCleaningWaitingHelpCommand(identity.TenantId, identity.UserId, cleaningId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : HousekeepingResultHttpMapper.ToActionResult(result.Error);
    }

    internal static CleaningDetailResponse ToDetailResponse(CleaningResult result) => new(
        result.Id, result.PropertyId, result.ReservationId, result.AssignedHousekeeperUserId, result.Status,
        result.CreatedByUserId, result.CreatedAtUtc, result.ScheduledAtUtc, result.StartedAtUtc,
        result.InspectionStartedAtUtc, result.CompletedAtUtc, result.CancelledAtUtc);

    private static PagedCleaningResponse ToResponse(PagedResult<CleaningSummaryResult> result) => new(
        result.Page, result.PageSize, result.TotalCount, result.Items.Select(ToResponse).ToArray());

    internal static CleaningSummaryResponse ToResponse(CleaningSummaryResult result) => new(
        result.Id, result.PropertyId, result.ReservationId, result.AssignedHousekeeperUserId, result.Status,
        result.CreatedAtUtc, result.ScheduledAtUtc);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
