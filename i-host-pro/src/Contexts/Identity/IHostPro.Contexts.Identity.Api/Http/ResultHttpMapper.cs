using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Identity.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any Identity &amp; Access command/query becomes an HTTP response
/// (Incremento 2 plan, Etapa 14; extended Incremento 3, Checkpoint 4). No
/// controller action inspects an <see cref="Error"/> itself — every one of
/// them calls only this.
///
/// The generic-auth-failure codes below are the small, fixed, already-approved
/// set of error codes the Login/Refresh handlers and their tenant-bootstrap
/// resolvers ("Tenant.NotFound") use specifically to signal "reject with a
/// generic, indistinguishable 401" (Etapa 9/10's own "resposta externa
/// genérica" requirement). <see cref="NotFoundErrorCodes"/> is the equivalent
/// closed allowlist for "reject with 404" (Incremento 3, Checkpoint 4:
/// <see cref="IdentityErrorCodes.SessionNotOwnedByUser"/> deliberately maps
/// to the SAME 404 regardless of the specific reason — nonexistent, foreign,
/// or already-inactive session — to avoid a session-id enumeration oracle).
/// <see cref="ConflictErrorCodes"/> (Incremento 3, Checkpoint 5; extended
/// Checkpoints 6, 7, 8 and 9) is the same idea for "reject with 409" —
/// <see cref="IdentityErrorCodes.EmailAlreadyInUse"/>/<see cref="IdentityErrorCodes.RoleAlreadyAssigned"/>/
/// <see cref="IdentityErrorCodes.RoleNotAssigned"/>/<see cref="IdentityErrorCodes.UserMustHaveAtLeastOneRole"/>/
/// <see cref="IdentityErrorCodes.LastActiveAdministrator"/>/<see cref="IdentityErrorCodes.UserAlreadyBlocked"/>/
/// <see cref="IdentityErrorCodes.UserAlreadyActive"/>/<see cref="IdentityErrorCodes.UserConcurrencyConflict"/>/
/// <see cref="IdentityErrorCodes.AdminCannotResetOwnPassword"/>.
/// Every other code reaching here is, by construction, either one of
/// <c>ValidationBehavior</c>'s stable ASCII FluentValidation codes, or a
/// handler-level code deliberately left OUT of every set above so it falls
/// through to the generic 400 branch (e.g. <c>email_invalid_format</c>,
/// <see cref="IdentityErrorCodes.NoChangesProvided"/>, <see cref="IdentityErrorCodes.InvalidCurrentPassword"/>,
/// <see cref="IdentityErrorCodes.NewPasswordMustDiffer"/> — all already
/// "just a bad request", needing no dedicated status). This is a closed,
/// intentional allowlist, not a heuristic: <see cref="Result.Error"/>/<see cref="Error"/>
/// carry no field distinguishing "validation" from
/// "authentication"/"not found"/"conflict" failures on their own, and
/// changing that shared, cross-context contract is out of scope. A future
/// command that also needs a generic response of one of these kinds must add
/// its own code to the relevant set here explicitly.
/// </summary>
public static class ResultHttpMapper
{
    private static readonly HashSet<string> GenericAuthFailureCodes = new(StringComparer.Ordinal)
    {
        "login_invalid_credentials",
        "refresh_token_invalid",
        "Tenant.NotFound",
    };

    private static readonly HashSet<string> NotFoundErrorCodes = new(StringComparer.Ordinal)
    {
        IdentityErrorCodes.SessionNotOwnedByUser,
        IdentityErrorCodes.AuthenticatedUserNotFound,
        IdentityErrorCodes.RoleNotFound,
        IdentityErrorCodes.UserNotFound,
    };

    private static readonly HashSet<string> ConflictErrorCodes = new(StringComparer.Ordinal)
    {
        IdentityErrorCodes.EmailAlreadyInUse,
        IdentityErrorCodes.RoleAlreadyAssigned,
        IdentityErrorCodes.RoleNotAssigned,
        IdentityErrorCodes.UserMustHaveAtLeastOneRole,
        IdentityErrorCodes.LastActiveAdministrator,
        IdentityErrorCodes.UserAlreadyBlocked,
        IdentityErrorCodes.UserAlreadyActive,
        IdentityErrorCodes.UserConcurrencyConflict,
        IdentityErrorCodes.AdminCannotResetOwnPassword,
    };

    public static IActionResult ToActionResult(Error error)
    {
        if (GenericAuthFailureCodes.Contains(error.Code))
        {
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "invalid_credentials",
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
        }

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
        // with a comma (never the rejected value itself) — split back into
        // an array of stable ASCII codes for the client, never the free-text
        // Error.Message alone.
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
