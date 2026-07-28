using IHostPro.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Identity.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any of the three auth commands becomes an HTTP response (Incremento
/// 2 plan, Etapa 14). No controller action inspects an <see cref="Error"/>
/// itself — every one of them calls only this.
///
/// The generic-auth-failure codes below are the small, fixed, already-approved
/// set of error codes the Login/Refresh handlers and their tenant-bootstrap
/// resolvers ("Tenant.NotFound") use specifically to signal "reject with a
/// generic, indistinguishable 401" (Etapa 9/10's own "resposta externa
/// genérica" requirement) — every other code reaching here is, by
/// construction, one of <c>ValidationBehavior</c>'s stable ASCII
/// FluentValidation codes. This is a closed, intentional allowlist, not a
/// heuristic: <see cref="Result.Error"/>/<see cref="Error"/> carry no field
/// distinguishing "validation" from "authentication" failures on their own,
/// and changing that shared, cross-context contract is out of scope for this
/// etapa. A future auth-adjacent command that also needs a generic-401
/// response must add its own code here explicitly.
/// </summary>
public static class ResultHttpMapper
{
    private static readonly HashSet<string> GenericAuthFailureCodes = new(StringComparer.Ordinal)
    {
        "login_invalid_credentials",
        "refresh_token_invalid",
        "Tenant.NotFound",
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
