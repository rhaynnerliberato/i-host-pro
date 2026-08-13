using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Reservations.Api.Contracts;
using IHostPro.Contexts.Reservations.Api.Http;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Schedule;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Reservations.Api.Controllers;

/// <summary>
/// Read-only, unified Agenda (Fase 7, Incremento 1 — Agenda Foundation,
/// Checkpoint 1) — composes Reservations (this Bounded Context's own data)
/// and Cleanings (Housekeeping's own data, via the local
/// <c>CleaningScheduleProjection</c> read-model) into one view. No
/// POST/PUT/PATCH/DELETE this increment — no write use case has been
/// approved yet (Checkpoint 1, item 17 of the approval: "Agenda não deve
/// virar um bypass das APIs/aggregates de origem").
///
/// Authorized by EITHER <see cref="IdentityPermissionCodes.ScheduleManage"/>
/// (ADMIN/OPERATOR) OR <see cref="IdentityPermissionCodes.ScheduleRead"/>
/// (HOUSEKEEPER/AI_AGENT) — checked manually via <see cref="IAuthorizationService"/>
/// rather than a single <c>[Authorize(Policy=...)]</c> attribute, since this
/// codebase's existing policies are always exact single-code requirements
/// (never an OR of two): stacking two <c>[Authorize]</c> attributes would AND
/// them, denying every role that holds only one of the two codes.
/// <c>SCHEDULE:READ:OWN_OWNER</c> is deliberately NOT included (and not yet
/// promoted to a constant) — Property Owner access remains deferred this
/// increment (Checkpoint 0 gate: no owner→property mapping exists inside
/// Reservation &amp; Scheduling yet).
/// </summary>
[ApiController]
[Route("api/v1/schedule")]
[Authorize]
public sealed class ScheduleController : ControllerBase
{
    private readonly IReservationsRequestDispatcher _sender;
    private readonly IAuthorizationService _authorizationService;

    public ScheduleController(IReservationsRequestDispatcher sender, IAuthorizationService authorizationService)
    {
        _sender = sender;
        _authorizationService = authorizationService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ScheduleItemResponse[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? propertyId, [FromQuery] Guid? housekeeperUserId, [FromQuery] string? eventType,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!ReservationsIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var manageAuthorization = await _authorizationService.AuthorizeAsync(User, IdentityPermissionCodes.ScheduleManage);
        if (!manageAuthorization.Succeeded)
        {
            var readAuthorization = await _authorizationService.AuthorizeAsync(User, IdentityPermissionCodes.ScheduleRead);
            if (!readAuthorization.Succeeded)
                return Forbid();
        }

        var result = await _sender.Send(
            new ListScheduleQuery(from ?? default, to ?? default, propertyId, housekeeperUserId, eventType), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value.Select(ToResponse).ToArray())
            : ReservationsResultHttpMapper.ToActionResult(result.Error);
    }

    private static ScheduleItemResponse ToResponse(ScheduleItemResult item) => new(
        item.Id, item.Type, item.PropertyId, item.StartAtUtc, item.EndAtUtc, item.Status, item.HousekeeperUserId, item.SourceReferenceId);
}
