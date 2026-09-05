using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IHostPro.Worker.Observability;

/// <summary>
/// Fase 12, Checkpoint 5.3E (Observability Architecture) — closes the
/// signal gap for the three dependency-unavailability alerts in the
/// already-approved catalogue (Fase 12 §4.6: Postgres/RabbitMQ/Redis
/// indisponível) that had no reliable native AWS CloudWatch metric. Mirrors
/// <c>DeadLetterMetricsBackgroundService</c>'s own established pattern
/// exactly — a periodic poll into an in-memory cache, read synchronously by
/// an <see cref="ObservableGauge{T}"/> callback, which must never itself
/// touch a real dependency.
///
/// Reuses the SAME <see cref="HealthCheckService"/> already registered by
/// <c>AddHealthChecks()</c> — never a second, parallel set of connection
/// checks. Filters to the "ready"-tagged checks only (the same tag
/// <c>/health/ready</c> itself uses). Publishes only component name and a
/// 1/0 healthy flag — never <c>HealthReportEntry.Description</c>/
/// <c>Exception</c>, which could leak a connection string or a raw driver
/// exception message (same discipline as <see cref="ObservabilityHealthCheckResponseWriter"/>).
/// </summary>
public sealed class DependencyHealthMetricsBackgroundService : BackgroundService
{
    private static readonly ConcurrentDictionary<string, int> Statuses = new();
    private static readonly Meter Meter = new("IHostPro.DependencyHealth");

    static DependencyHealthMetricsBackgroundService() =>
        Meter.CreateObservableGauge("dependency.health", () =>
            Statuses.Select(kv => new Measurement<int>(kv.Value, new KeyValuePair<string, object?>("component", kv.Key))));

    private readonly HealthCheckService _healthCheckService;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<DependencyHealthMetricsBackgroundService> _logger;

    public DependencyHealthMetricsBackgroundService(HealthCheckService healthCheckService, ILogger<DependencyHealthMetricsBackgroundService> logger)
    {
        _healthCheckService = healthCheckService;
        _pollInterval = TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _healthCheckService.CheckHealthAsync(check => check.Tags.Contains("ready"), cancellationToken);
            foreach (var (name, entry) in report.Entries)
                Statuses[name] = entry.Status == HealthStatus.Healthy ? 1 : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh dependency health metrics — gauge left at its last known value.");
        }
    }
}
