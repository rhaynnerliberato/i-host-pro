using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Housekeeping.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any Housekeeping command/query becomes an HTTP response (Fase 6,
/// Incremento 1, approval §16) — mirrors <c>ReservationsResultHttpMapper</c>'s
/// role, but exposes the SPECIFIC stable code to the client for every
/// mapped branch (<c>ProblemDetails.Extensions["code"]</c>), per the
/// increment's own explicit code list — unlike Reservations' generic
/// "not_found"/"conflict" titles. No controller action inspects an
/// <see cref="Error"/> itself — every one of them calls only this.
/// </summary>
public static class HousekeepingResultHttpMapper
{
    private static readonly HashSet<string> NotFoundErrorCodes = new(StringComparer.Ordinal)
    {
        HousekeepingErrorCodes.CleaningNotFound,
        HousekeepingErrorCodes.PropertyNotFound,
        HousekeepingErrorCodes.ReservationReferenceNotAvailable,
    };

    private static readonly HashSet<string> ConflictErrorCodes = new(StringComparer.Ordinal)
    {
        HousekeepingErrorCodes.InvalidCleaningTransition,
        HousekeepingErrorCodes.CleaningConcurrencyConflict,
    };

    private static readonly HashSet<string> ForbiddenErrorCodes = new(StringComparer.Ordinal)
    {
        HousekeepingErrorCodes.HousekeeperNotEligible,
    };

    public static IActionResult ToActionResult(Error error)
    {
        if (NotFoundErrorCodes.Contains(error.Code))
            return Problem(StatusCodes.Status404NotFound, "not_found", error.Code);

        if (ConflictErrorCodes.Contains(error.Code))
            return Problem(StatusCodes.Status409Conflict, "conflict", error.Code);

        if (ForbiddenErrorCodes.Contains(error.Code))
            return Problem(StatusCodes.Status403Forbidden, "forbidden", error.Code);

        // ValidationBehavior joins every FluentValidation failure's ErrorCode
        // with a comma — split back into an array of stable ASCII codes for
        // the client, never the free-text Error.Message alone.
        var codes = error.Code.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "validation_error",
        };
        problem.Extensions["codes"] = codes;

        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
    }

    private static IActionResult Problem(int statusCode, string title, string code)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
        };
        problem.Extensions["code"] = code;

        return new ObjectResult(problem) { StatusCode = statusCode };
    }
}
