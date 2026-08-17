using IHostPro.Contexts.Dashboard.Api.Contracts;
using IHostPro.Contexts.Dashboard.Api.Http;
using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Dashboard.Application.Overview;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Dashboard.Api.Controllers;

/// <summary>
/// Read-only operational Overview (Fase 7, Incremento 2, Checkpoint 2) — a
/// single aggregated endpoint over the Dashboard &amp; Reporting projections
/// (Reservations/Housekeeping/Properties/Occurrences). No per-card endpoint
/// exists this MVP. No POST/PUT/PATCH/DELETE — this Bounded Context has no
/// write use case (it is a terminal, event-consumer-only context —
/// Architecture Principles §14).
///
/// Authorized by EITHER <see cref="IdentityPermissionCodes.DashboardManage"/>
/// (ADMIN) OR <see cref="IdentityPermissionCodes.DashboardRead"/> (OPERATOR)
/// — checked manually via <see cref="IAuthorizationService"/> rather than a
/// single <c>[Authorize(Policy=...)]</c> attribute, mirrors
/// <c>ScheduleController</c>'s own precedent exactly (this codebase's
/// existing policies are always exact single-code requirements — stacking
/// two <c>[Authorize]</c> attributes would AND them, denying every role that
/// holds only one of the two codes). <c>DASHBOARD:READ:OWN_OWNER</c> is
/// deliberately NOT included — Property Owner access remains deferred this
/// checkpoint (mandate §30 scope gate).
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardRequestDispatcher _sender;
    private readonly IAuthorizationService _authorizationService;

    public DashboardController(IDashboardRequestDispatcher sender, IAuthorizationService authorizationService)
    {
        _sender = sender;
        _authorizationService = authorizationService;
    }

    [HttpGet("overview")]
    [ProducesResponseType(typeof(DashboardOverviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Overview(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!DashboardIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var manageAuthorization = await _authorizationService.AuthorizeAsync(User, IdentityPermissionCodes.DashboardManage);
        if (!manageAuthorization.Succeeded)
        {
            var readAuthorization = await _authorizationService.AuthorizeAsync(User, IdentityPermissionCodes.DashboardRead);
            if (!readAuthorization.Succeeded)
                return Forbid();
        }

        var result = await _sender.Send(new GetDashboardOverviewQuery(from ?? default, to ?? default), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : DashboardResultHttpMapper.ToActionResult(result.Error);
    }

    private static DashboardOverviewResponse ToResponse(DashboardOverviewResult overview) => new(
        new DashboardPeriodResponse(overview.Period.From, overview.Period.To),
        new DashboardReservationsOverviewResponse(
            overview.Reservations.CheckInsInPeriod,
            overview.Reservations.CheckOutsInPeriod,
            overview.Reservations.FutureReservations,
            overview.Reservations.CancelledInPeriod,
            overview.Reservations.StatusCounts
                .Select(s => new DashboardStatusCountResponse(s.Status, s.Count))
                .ToArray()),
        new DashboardHousekeepingOverviewResponse(
            overview.Housekeeping.Pending,
            overview.Housekeeping.InProgress,
            overview.Housekeeping.Interrupted,
            overview.Housekeeping.CompletedInPeriod,
            overview.Housekeeping.CancelledInPeriod,
            overview.Housekeeping.Delayed,
            overview.Housekeeping.WaitingHelp,
            overview.Housekeeping.WaitingMaterials),
        new DashboardPropertiesOverviewResponse(
            overview.Properties.Active, overview.Properties.Inactive, overview.Properties.Archived),
        new DashboardOccurrencesOverviewResponse(
            overview.Occurrences.TotalInPeriod,
            overview.Occurrences.ByType
                .Select(t => new DashboardOccurrenceTypeCountResponse(t.Type, t.Count))
                .ToArray()),
        overview.GeneratedAtUtc);
}
