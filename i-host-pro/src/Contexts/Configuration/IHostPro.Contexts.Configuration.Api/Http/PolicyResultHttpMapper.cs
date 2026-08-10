using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Configuration.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any Policies command/query becomes an HTTP response — mirrors
/// <c>ReservationsResultHttpMapper</c>'s structure, but the seven
/// ProblemDetails outcomes Fase 5, Incremento 1 official decisions §8
/// explicitly requires are each distinguished by their own <c>Title</c> (not
/// collapsed into generic "not_found"/"conflict"/"validation_failed" buckets
/// like Reservations): <c>policy_not_found</c>; <c>invalid_policy_value</c>;
/// <c>scope_not_supported</c>; <c>policy_not_configured</c>;
/// <c>version_conflict</c>; <c>forbidden</c>; <c>validation_error</c> (the
/// fallback for anything else, e.g. FluentValidation's own codes). No
/// controller action inspects an <see cref="Error"/> itself — every one of
/// them calls only this.
/// </summary>
public static class PolicyResultHttpMapper
{
    public static IActionResult ToActionResult(Error error)
    {
        var (status, title) = error.Code switch
        {
            PolicyErrorCodes.PolicyNotFound => (StatusCodes.Status404NotFound, "policy_not_found"),
            PolicyErrorCodes.InvalidPolicyValue => (StatusCodes.Status400BadRequest, "invalid_policy_value"),
            PolicyErrorCodes.ScopeNotSupported => (StatusCodes.Status400BadRequest, "scope_not_supported"),
            PolicyErrorCodes.PolicyNotConfigured => (StatusCodes.Status404NotFound, "policy_not_configured"),
            PolicyErrorCodes.VersionConflict => (StatusCodes.Status409Conflict, "version_conflict"),
            PolicyErrorCodes.Forbidden => (StatusCodes.Status403Forbidden, "forbidden"),
            _ => (StatusCodes.Status400BadRequest, "validation_error"),
        };

        var problem = new ProblemDetails { Status = status, Title = title };

        // ValidationBehavior joins every FluentValidation failure's
        // ErrorCode with a comma — surfaced only for the generic
        // validation_error fallback, never for one of the six named codes
        // above (each of those is always a single, ad-hoc handler-level
        // code, never comma-joined).
        if (title == "validation_error")
        {
            var codes = error.Code.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            problem.Extensions["codes"] = codes;
        }

        return new ObjectResult(problem) { StatusCode = status };
    }
}
