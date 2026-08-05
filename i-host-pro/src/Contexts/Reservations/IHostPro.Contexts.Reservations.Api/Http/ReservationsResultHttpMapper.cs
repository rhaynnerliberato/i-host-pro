using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Application.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Reservations.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any Reservations command/query becomes an HTTP response — mirrors
/// <c>PropertyManagement.Api.Http.PropertyManagementResultHttpMapper</c>
/// exactly. No controller action inspects an <see cref="Error"/> itself —
/// every one of them calls only this.
///
/// <see cref="NotFoundErrorCodes"/>/<see cref="ConflictErrorCodes"/> are
/// small, closed, intentional allowlists — every other code reaching here
/// (FluentValidation's stable codes, or a handler-level ad-hoc code
/// deliberately left out, e.g. <c>reservation_schedule_invalid</c>,
/// <c>guest_name_invalid</c>) falls through to the generic 400 branch (Fase
/// 3, Incremento 1 plan, item 10: "validação/capacidade/Imóvel inativo →
/// 400"). Never reveals whether a cross-tenant resource exists (item 10).
/// </summary>
public static class ReservationsResultHttpMapper
{
    private static readonly HashSet<string> NotFoundErrorCodes = new(StringComparer.Ordinal)
    {
        ReservationsErrorCodes.ReservationNotFound,
        ReservationsErrorCodes.PropertyNotFound,
    };

    private static readonly HashSet<string> ConflictErrorCodes = new(StringComparer.Ordinal)
    {
        ReservationsErrorCodes.ReservationDateConflict,
        ReservationsErrorCodes.ReservationAlreadyCancelled,
        ReservationsErrorCodes.CancelledReservationCannotBeModified,
        ReservationsErrorCodes.ReservationConcurrencyConflict,
    };

    public static IActionResult ToActionResult(Error error)
    {
        if (NotFoundErrorCodes.Contains(error.Code))
        {
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "not_found",
            })
            {
                StatusCode = StatusCodes.Status404NotFound,
            };
        }

        if (ConflictErrorCodes.Contains(error.Code))
        {
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "conflict",
            })
            {
                StatusCode = StatusCodes.Status409Conflict,
            };
        }

        // ValidationBehavior joins every FluentValidation failure's ErrorCode
        // with a comma — split back into an array of stable ASCII codes for
        // the client, never the free-text Error.Message alone. A
        // handler-level single code (e.g. PropertyNotActive,
        // PropertyCapacityExceeded, NoChangesProvided, or an ad-hoc
        // domain-guard code) also survives this split unchanged (no comma
        // present).
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
