using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.PropertyManagement.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any Property Management command/query becomes an HTTP response
/// (Checkpoint 2 plan, item 8) — mirrors Identity's own
/// <c>ResultHttpMapper</c>. No controller action inspects an
/// <see cref="Error"/> itself — every one of them calls only this.
///
/// <see cref="NotFoundErrorCodes"/>/<see cref="ConflictErrorCodes"/> are
/// small, closed, intentional allowlists — every other code reaching here
/// (FluentValidation's stable codes, or a handler-level code deliberately
/// left out, e.g. <see cref="PropertyManagementErrorCodes.NoChangesProvided"/>,
/// <c>condominium_name_invalid</c>, <c>condominium_address_invalid</c>) falls
/// through to the generic 400 branch.
/// </summary>
public static class PropertyManagementResultHttpMapper
{
    private static readonly HashSet<string> NotFoundErrorCodes = new(StringComparer.Ordinal)
    {
        PropertyManagementErrorCodes.CondominiumNotFound,
        PropertyManagementErrorCodes.PropertyNotFound,
        PropertyManagementErrorCodes.OwnerUserNotFound,
        PropertyManagementErrorCodes.PropertyOwnerNotLinked,
        PropertyManagementErrorCodes.FrontDeskContactNotFound,
    };

    private static readonly HashSet<string> ConflictErrorCodes = new(StringComparer.Ordinal)
    {
        PropertyManagementErrorCodes.CondominiumConcurrencyConflict,
        PropertyManagementErrorCodes.PropertyCodeAlreadyExists,
        PropertyManagementErrorCodes.PropertyConcurrencyConflict,
        PropertyManagementErrorCodes.PropertyAlreadyActive,
        PropertyManagementErrorCodes.PropertyAlreadyInactive,
        PropertyManagementErrorCodes.PropertyAlreadyArchived,
        PropertyManagementErrorCodes.InvalidPropertyStatusTransition,
        PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified,
        PropertyManagementErrorCodes.OwnerUserNotEligible,
        PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked,
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
        // handler-level single code (e.g. NoChangesProvided) also survives
        // this split unchanged (no comma present).
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
