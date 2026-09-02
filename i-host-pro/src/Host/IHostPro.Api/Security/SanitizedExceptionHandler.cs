using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Api.Security;

/// <summary>
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — the single
/// place an UNHANDLED exception becomes an HTTP response. Every EXPECTED
/// failure already goes through <c>ResultHttpMapper</c> with its own safe,
/// specific <see cref="ProblemDetails"/> shape; this handler exists only for
/// the case a handler/middleware throws something nobody anticipated. The
/// response body is always the same fixed, generic detail string — never
/// <see cref="Exception.Message"/>, never a stack trace, never any inner
/// exception, regardless of environment (mandate §5: "Production response
/// NÃO pode expor... exception internals" — this class never distinguishes
/// Development vs Production in what it RETURNS to the caller, only in what
/// it logs). The real exception is logged server-side, with a
/// <c>traceId</c> in both the log and the response so the two can be
/// correlated without ever leaking exception internals over the wire.
/// </summary>
public sealed class SanitizedExceptionHandler : IExceptionHandler
{
    private const string GenericDetail = "An unexpected error occurred while processing the request.";

    private readonly ILogger<SanitizedExceptionHandler> _logger;

    public SanitizedExceptionHandler(ILogger<SanitizedExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = httpContext.TraceIdentifier;

        _logger.LogError(exception, "Unhandled exception. TraceId {TraceId}, Path {Path}", traceId, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Detail = GenericDetail,
            Extensions = { ["traceId"] = traceId },
        };

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        return true;
    }
}
