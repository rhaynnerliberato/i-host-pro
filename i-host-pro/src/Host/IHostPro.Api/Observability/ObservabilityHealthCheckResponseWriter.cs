using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IHostPro.Api.Observability;

/// <summary>
/// Fase 12, Checkpoint 2 (Observability Finalization, Documento 21 §18) —
/// the default ASP.NET Core health check response writer returns only a
/// plain-text overall status, which is safe but uninformative; the built-in
/// alternative (<c>UI.Client</c>'s JSON writer) serializes every
/// <see cref="HealthReportEntry.Description"/>/<see cref="HealthReportEntry.Exception"/>,
/// which can surface a connection string or a raw driver exception message.
/// This writer emits exactly what the mandate allows — component name,
/// status, duration — and nothing else, for every environment, never only
/// in Development.
/// </summary>
internal static class ObservabilityHealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            components = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, SerializerOptions));
    }
}
