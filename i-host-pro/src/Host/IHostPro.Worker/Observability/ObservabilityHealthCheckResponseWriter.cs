using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IHostPro.Worker.Observability;

/// <summary>
/// Fase 12, Checkpoint 2 (Observability Finalization, Documento 21 §18) —
/// mirrors <c>IHostPro.Api.Observability.ObservabilityHealthCheckResponseWriter</c>
/// exactly (duplicated rather than shared from BuildingBlocks.Infrastructure,
/// which does not reference the ASP.NET Core shared framework and should
/// not gain that dependency platform-wide just for two Host-level health
/// endpoints). Emits component name, status, duration only — never
/// <see cref="HealthReportEntry.Description"/>/<see cref="HealthReportEntry.Exception"/>,
/// which could otherwise surface a connection string or a raw driver
/// exception message.
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
