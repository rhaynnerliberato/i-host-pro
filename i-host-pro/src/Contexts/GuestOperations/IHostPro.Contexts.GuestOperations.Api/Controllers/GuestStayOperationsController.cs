using IHostPro.Contexts.GuestOperations.Api.Contracts;
using IHostPro.Contexts.GuestOperations.Api.Http;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.GuestOperations.Api.Controllers;

/// <summary>
/// Check-in/checkout for a Reservation's <c>GuestStayOperation</c> (Fase 10,
/// Checkpoint 2 — Check-in/Checkout Core) — mirrors <c>ReservationsController</c>'s
/// shape. Exactly the two endpoints this checkpoint authorizes; no check-in
/// form, access credential, Early Check-in/Late Checkout or Portaria
/// endpoint exists here (deferred to later checkpoints). Every action
/// requires <see cref="IdentityPermissionCodes.GuestOperationsManage"/> (the
/// first real consumer of this policy — no read-only endpoint exists, so no
/// <c>GUEST_OPERATIONS:READ</c> policy is registered). Every action reads
/// the actor exclusively from <see cref="GuestOperationsIdentityReader"/> —
/// never a request-body-supplied tenant/actor id — and only builds a
/// Command before dispatching through <see cref="IGuestOperationsRequestDispatcher"/>.
/// Neither action accepts a request body.
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

    private static GuestStayOperationResponse ToResponse(GuestStayOperationResult result) => new(
        result.Id, result.ReservationId, result.PropertyId, result.Status,
        result.CheckedInAtUtc, result.CheckedOutAtUtc, result.CreatedAtUtc, result.UpdatedAtUtc);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
