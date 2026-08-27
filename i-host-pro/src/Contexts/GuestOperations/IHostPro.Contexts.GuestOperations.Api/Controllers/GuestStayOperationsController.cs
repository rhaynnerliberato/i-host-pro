using IHostPro.Contexts.GuestOperations.Api.Contracts;
using IHostPro.Contexts.GuestOperations.Api.Http;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.GuestOperations.Api.Controllers;

/// <summary>
/// Check-in/checkout, and (Fase 10, Checkpoint 3) Early Check-in/Late
/// Checkout requests, for a Reservation's <c>GuestStayOperation</c> — mirrors
/// <c>ReservationsController</c>'s shape. No check-in form, access
/// credential, or Portaria endpoint exists here (deferred to later
/// checkpoints); no separate approve/deny endpoint either — the Early/Late
/// endpoints decide automatically, in the same request as their own
/// creation (mandate decision). Every action requires
/// <see cref="IdentityPermissionCodes.GuestOperationsManage"/> (no read-only
/// endpoint exists, so no <c>GUEST_OPERATIONS:READ</c> policy is
/// registered). Every action reads the actor exclusively from
/// <see cref="GuestOperationsIdentityReader"/> — never a request-body-supplied
/// tenant/actor id — and only builds a Command before dispatching through
/// <see cref="IGuestOperationsRequestDispatcher"/>. Check-in/checkout accept
/// no request body; the two Checkpoint 3 endpoints accept only the guest's
/// requested new time.
/// </summary>
[ApiController]
[Route("api/v1/guest-operations/reservations")]
public sealed class GuestStayOperationsController : ControllerBase
{
    private readonly IGuestOperationsRequestDispatcher _sender;

    public GuestStayOperationsController(IGuestOperationsRequestDispatcher sender) => _sender = sender;

    [HttpPost("{reservationId:guid}/check-in")]
    [Authorize(Policy = IdentityPermissionCodes.GuestOperationsManage)]
    [ProducesResponseType(typeof(GuestStayOperationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CheckIn(Guid reservationId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!GuestOperationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new RecordGuestCheckedInCommand { TenantId = identity.TenantId, ReservationId = reservationId };
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : GuestOperationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{reservationId:guid}/checkout")]
    [Authorize(Policy = IdentityPermissionCodes.GuestOperationsManage)]
    [ProducesResponseType(typeof(GuestStayOperationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CheckOut(Guid reservationId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!GuestOperationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new RecordGuestCheckedOutCommand { TenantId = identity.TenantId, ReservationId = reservationId };
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : GuestOperationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{reservationId:guid}/early-check-in")]
    [Authorize(Policy = IdentityPermissionCodes.GuestOperationsManage)]
    [ProducesResponseType(typeof(EarlyCheckInRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestEarlyCheckIn(
        Guid reservationId, RequestEarlyCheckInHttpRequest body, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!GuestOperationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new RequestEarlyCheckInCommand
        {
            TenantId = identity.TenantId,
            ReservationId = reservationId,
            RequestedCheckInAt = body.RequestedCheckInAt,
        };
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : GuestOperationsResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{reservationId:guid}/late-checkout")]
    [Authorize(Policy = IdentityPermissionCodes.GuestOperationsManage)]
    [ProducesResponseType(typeof(LateCheckoutRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestLateCheckout(
        Guid reservationId, RequestLateCheckoutHttpRequest body, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!GuestOperationsIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new RequestLateCheckoutCommand
        {
            TenantId = identity.TenantId,
            ReservationId = reservationId,
            RequestedCheckOutAt = body.RequestedCheckOutAt,
        };
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : GuestOperationsResultHttpMapper.ToActionResult(result.Error);
    }

    private static GuestStayOperationResponse ToResponse(GuestStayOperationResult result) => new(
        result.Id, result.ReservationId, result.PropertyId, result.Status,
        result.CheckedInAtUtc, result.CheckedOutAtUtc, result.CreatedAtUtc, result.UpdatedAtUtc);

    private static EarlyCheckInRequestResponse ToResponse(EarlyCheckInRequestResult result) => new(
        result.Id, result.ReservationId, result.RequestedCheckInAt, result.Status, result.DenialReasonCode,
        result.CreatedAtUtc, result.DecidedAtUtc, result.UpdatedAtUtc);

    private static LateCheckoutRequestResponse ToResponse(LateCheckoutRequestResult result) => new(
        result.Id, result.ReservationId, result.RequestedCheckOutAt, result.ChargeType, result.ChargeValue, result.RequiresPix,
        result.Status, result.DenialReasonCode, result.CreatedAtUtc, result.DecidedAtUtc, result.UpdatedAtUtc);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
