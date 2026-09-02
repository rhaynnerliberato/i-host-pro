using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IHostPro.Worker.Observability;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting) — closes the CP3
/// mandate item named explicitly in the CP2 alert catalogue ("Acúmulo em
/// dead-letter... nenhuma ferramenta de monitoramento/replay construída, CP3
/// mandato item 21"): <c>DeadLetterObservable=true</c>. Exposes only a
/// per-schema COUNT via <c>wolverine.dead_letters</c> (a gauge, low
/// cardinality — schema name only) — never the message body, message type,
/// or failure reason (mandate §15/§16: "NUNCA expor payload"). Deliberately
/// NOT wired into any health check's status (mandate §17 — a dead letter's
/// mere historical existence must never flip readiness to Unhealthy; a real
/// production alert threshold on this metric is a separate, future decision,
/// registered as <c>DlqHealthThreshold=TBD_FOR_PRODUCTION</c>). No
/// replay/admin tooling is built here either — <c>DlqReplayAdministrativeCapability=DEFERRED</c>,
/// exactly as the mandate explicitly allows given this metric exists.
///
/// Queries Wolverine's own <c>wolverine_dead_letters</c> table — one per
/// ancillary schema this Worker enrolls (see <c>Program.cs</c>'s own
/// <c>EnrollAncillaryPostgresqlOutbox</c> calls) — on a fixed interval, into
/// an in-memory cache the <see cref="ObservableGauge{T}"/> callback reads
/// synchronously; the callback itself never touches the database (an
/// OTel-observable-instrument callback must be synchronous and fast).
/// </summary>
public sealed class DeadLetterMetricsBackgroundService : BackgroundService
{
    /// <summary>Worker's own ancillary "_messaging" schemas — mirrors exactly the EnrollAncillaryPostgresqlOutbox calls in Program.cs.</summary>
    private static readonly string[] Schemas =
    [
        "housekeeping_messaging",
        "reservations_messaging",
        "dashboard_messaging",
        "guest_operations_messaging",
        "payments_messaging",
        "communication_messaging",
        "ai_agent_messaging",
    ];

    private static readonly ConcurrentDictionary<string, long> Counts = new();
    private static readonly Meter Meter = new("IHostPro.Wolverine");

    static DeadLetterMetricsBackgroundService() =>
        Meter.CreateObservableGauge("wolverine.dead_letters", () =>
            Counts.Select(kv => new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("schema", kv.Key))));

    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<DeadLetterMetricsBackgroundService> _logger;

    public DeadLetterMetricsBackgroundService(IConfiguration configuration, ILogger<DeadLetterMetricsBackgroundService> logger)
    {
        _connectionString = configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Platform'.");
        _pollInterval = TimeSpan.FromSeconds(60);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var schema in Schemas)
                await RefreshCountAsync(schema, stoppingToken);

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

    private async Task RefreshCountAsync(string schema, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // schema is one of the fixed literals in Schemas above — never external input.
            command.CommandText = $"SELECT count(*) FROM {schema}.wolverine_dead_letters";
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            Counts[schema] = count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh the dead-letter count for schema {Schema} — metric left at its last known value.", schema);
        }
    }
}
