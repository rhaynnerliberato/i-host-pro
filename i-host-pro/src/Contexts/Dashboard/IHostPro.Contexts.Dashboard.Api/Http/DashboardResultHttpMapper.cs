using IHostPro.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Dashboard.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any Dashboard query becomes an HTTP response — mirrors
/// <c>Reservations.Api.Http.ReservationsResultHttpMapper</c>. No NotFound/
/// Conflict allowlist exists here (unlike Reservations') — the Overview
/// query has exactly one possible failure mode this checkpoint: an invalid
/// interval, always FluentValidation-sourced, always 400.
/// </summary>
public static class DashboardResultHttpMapper
{
    public static IActionResult ToActionResult(Error error)
    {
        // ValidationBehavior joins every FluentValidation failure's ErrorCode
        // with a comma — split back into an array of stable ASCII codes for
        // the client, never the free-text Error.Message alone.
        var codes = error.Code.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "validation_failed",
        };
        problem.Extensions["codes"] = codes;

        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
    }
}
