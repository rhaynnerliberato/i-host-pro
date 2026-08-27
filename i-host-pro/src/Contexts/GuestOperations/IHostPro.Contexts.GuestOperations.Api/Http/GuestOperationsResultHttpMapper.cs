using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.GuestOperations.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any Guest Operations command becomes an HTTP response (Fase 10,
/// Checkpoint 2 — Check-in/Checkout Core) — mirrors
/// <c>Reservations.Api.Http.ReservationsResultHttpMapper</c> exactly. No
/// controller action inspects an <see cref="Error"/> itself — every one of
/// them calls only this.
/// </summary>
public static class GuestOperationsResultHttpMapper
{
    private static readonly HashSet<string> NotFoundErrorCodes = new(StringComparer.Ordinal)
    {
        GuestOperationsErrorCodes.GuestStayOperationNotFound,
    };

    private static readonly HashSet<string> ConflictErrorCodes = new(StringComparer.Ordinal)
    {
        GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut,
        GuestOperationsErrorCodes.GuestStayOperationNotCheckedIn,
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

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "validation_failed",
        };
        problem.Extensions["code"] = error.Code;

        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
    }
}
