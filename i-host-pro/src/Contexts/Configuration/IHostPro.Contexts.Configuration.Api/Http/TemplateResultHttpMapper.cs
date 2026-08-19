using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Configuration.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any Template command/query becomes an HTTP response — mirrors
/// <c>PolicyResultHttpMapper</c>'s structure exactly.
/// </summary>
public static class TemplateResultHttpMapper
{
    public static IActionResult ToActionResult(Error error)
    {
        var (status, title) = error.Code switch
        {
            TemplateErrorCodes.TemplateNotFound => (StatusCodes.Status404NotFound, "template_not_found"),
            TemplateErrorCodes.TemplateKeyAlreadyExists => (StatusCodes.Status409Conflict, "template_key_already_exists"),
            _ => (StatusCodes.Status400BadRequest, "validation_error"),
        };

        var problem = new ProblemDetails { Status = status, Title = title };

        if (title == "validation_error")
        {
            var codes = error.Code.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            problem.Extensions["codes"] = codes;
        }

        return new ObjectResult(problem) { StatusCode = status };
    }
}
