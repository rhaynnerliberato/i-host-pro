using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Reservations.Api.Contracts;
using IHostPro.Contexts.Reservations.Api.Http;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Reservations.Api.Controllers;

/// <summary>
/// Administrative reservation creation/update/listing/detail/cancellation
/// (Fase 3, Incremento 1 plan) — mirrors <c>PropertiesController</c>
/// exactly. Every action requires
/// <see cref="IdentityPermissionCodes.ReservationsManage"/> (same
/// centrally-registered policy, no duplicate registration here). Every
/// action reads the actor exclusively from <see cref="ReservationsIdentityReader"/>
/// — never a request-body-supplied tenant/actor id — and only builds a
/// Query/Command before dispatching through
/// <see cref="IReservationsRequestDispatcher"/>. Cancel accepts no body at
/// all — the controller never touches Infrastructure, never consults
/// Property Management directly, never creates auditoria or publishes an
/// event itself.
/// </summary>
[ApiController]
[Route("api/v1/reservations")]
public sealed class ReservationsController : ControllerBase
{
    private readonly IReservationsRequestDispatcher _sender;

    public ReservationsController(IReservationsRequestDispatcher sender) => _sender = sender;

    [HttpPost]
    [Authorize(Policy = IdentityPermissionCodes.ReservationsManage)]
    [ProducesResponseType(typeof(ReservationDetailResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateReservationRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ReservationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new CreateReservationCommand(
            identity.TenantId,
            identity.UserId,
            request.PropertyId ?? Guid.Empty,
            request.GuestName ?? string.Empty,
            request.GuestPhone,
            request.CheckInAt ?? default,
            request.CheckOutAt ?? default,
            request.GuestCount ?? 0);

        var result = await _sender.Send(command, cancellationToken);

        if (!result.IsSuccess)
            return ReservationsResultHttpMapper.ToActionResult(result.Error);

        var response = ToDetailResponse(result.Value);
        return CreatedAtAction(nameof(GetById), new { reservationId = response.Id }, response);
    }

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.ReservationsManage)]
    [ProducesResponseType(typeof(PagedReservationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? propertyId, [FromQuery] string? status,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ReservationsIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new ListReservationsQuery(propertyId, status, from, to, page, pageSize), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : ReservationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{reservationId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.ReservationsManage)]
    [ProducesResponseType(typeof(ReservationDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid reservationId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ReservationsIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new GetReservationDetailQuery(reservationId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : ReservationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPatch("{reservationId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.ReservationsManage)]
    [ProducesResponseType(typeof(ReservationDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid reservationId, [FromBody] UpdateReservationRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ReservationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new UpdateReservationCommand(
            identity.TenantId,
            identity.UserId,
            reservationId,
            request.PropertyId,
            request.GuestName,
            request.GuestPhone,
            request.CheckInAt,
            request.CheckOutAt,
            request.GuestCount);

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : ReservationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{reservationId:guid}/cancel")]
    [Authorize(Policy = IdentityPermissionCodes.ReservationsManage)]
    [ProducesResponseType(typeof(ReservationDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    // Named CancelReservation (not Cancel) as part of the OpenAPI operationId
    // stability gate (Fase 6, Checkpoint 6) — paired with an explicit
    // Program.cs CustomOperationIds assignment (see AddSwaggerGen) that gives
    // this action and CleaningsController.CancelCleaning deterministic,
    // collision-proof operationIds. The route itself
    // (POST /api/v1/reservations/{reservationId}/cancel) is unaffected.
    public async Task<IActionResult> CancelReservation(Guid reservationId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ReservationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new CancelReservationCommand(identity.TenantId, identity.UserId, reservationId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToDetailResponse(result.Value))
            : ReservationsResultHttpMapper.ToActionResult(result.Error);
    }

    private static ReservationDetailResponse ToDetailResponse(ReservationResult result) => new(
        result.Id, result.PropertyId, result.GuestName, result.GuestPhone, result.CheckInAt, result.CheckOutAt,
        result.GuestCount, result.Status, result.CreatedAt, result.UpdatedAt);

    private static PagedReservationResponse ToResponse(PagedResult<ReservationSummaryResult> result) => new(
        result.Page, result.PageSize, result.TotalCount, result.Items.Select(ToResponse).ToArray());

    private static ReservationSummaryResponse ToResponse(ReservationSummaryResult result) => new(
        result.Id, result.PropertyId, result.GuestName, result.CheckInAt, result.CheckOutAt, result.GuestCount,
        result.Status, result.CreatedAt, result.UpdatedAt);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
