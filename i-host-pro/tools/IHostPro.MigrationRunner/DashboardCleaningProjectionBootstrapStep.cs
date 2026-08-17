using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// One-time, idempotent deployment-time backfill of
/// <c>dashboard.cleaning_projection</c> from <c>housekeeping.cleanings</c>
/// (ADR-017; Fase 7, Incremento 2 — Dashboard &amp; Reporting Foundation,
/// Checkpoint 1). Same rationale and mechanism as
/// <see cref="PropertyProjectionBootstrap"/>/
/// <see cref="DashboardReservationProjectionBootstrapStep"/>.
///
/// <c>housekeeping.cleanings.status</c> stores the raw <c>CleaningStatus</c>
/// enum member name via <c>HasConversion&lt;string&gt;()</c> — those names
/// already exactly equal <c>CleaningStatusCodeMapper</c>'s stable codes
/// (Pending/Assigned/InTransit/Started/InInspection/Completed/Cancelled/
/// Interrupted/WaitingMaterials/WaitingHelp), so no case conversion is
/// applied here, unlike Reservation/Property status.
/// <c>LastEventAtUtc</c> is seeded with the bootstrap's own execution time
/// (<c>now()</c>) for the same reason documented on
/// <see cref="DashboardReservationProjectionBootstrapStep"/>.
///
/// <c>CancelledAtUtc</c> (Fase 7, Incremento 2, Checkpoint 2) is copied
/// directly from <c>housekeeping.cleanings.cancelled_at_utc</c> — a real,
/// dedicated column the <c>Cleaning</c> aggregate already maintains, no
/// reconstruction needed.
/// </summary>
public sealed class DashboardCleaningProjectionBootstrapStep : IProjectionBootstrapStep
{
    private readonly string _dashboardConnectionString;

    public DashboardCleaningProjectionBootstrapStep(string dashboardConnectionString) =>
        _dashboardConnectionString = dashboardConnectionString;

    public string Name => "dashboard.cleaning_projection";

    public async Task ExecuteAsync(ILogger log, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_dashboardConnectionString);
        await connection.OpenAsync(cancellationToken);

        var tenantIds = new List<Guid>();
        await using (var tenantsCommand = new NpgsqlCommand("SELECT id FROM identity.tenants", connection))
        await using (var reader = await tenantsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                tenantIds.Add(reader.GetGuid(0));
        }

        var totalInserted = 0;
        foreach (var tenantId in tenantIds)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var setTenantCommand = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id', $1, true)", connection, transaction))
            {
                setTenantCommand.Parameters.AddWithValue(tenantId.ToString());
                await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var backfillCommand = new NpgsqlCommand(
                """
                INSERT INTO dashboard.cleaning_projection
                    (tenant_id, cleaning_id, property_id, housekeeper_user_id, scheduled_at_utc, status, started_at_utc, completed_at_utc, cancelled_at_utc, last_event_at_utc)
                SELECT c.tenant_id, c.id, c.property_id, c.assigned_housekeeper_user_id, c.scheduled_at_utc, c.status, c.started_at_utc, c.completed_at_utc, c.cancelled_at_utc, now()
                FROM housekeeping.cleanings c
                ON CONFLICT (tenant_id, cleaning_id) DO NOTHING
                """,
                connection,
                transaction);

            totalInserted += await backfillCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        log.LogInformation(
            "Dashboard cleaning_projection backfill: {TenantCount} tenant(s) checked, {RowCount} row(s) inserted",
            tenantIds.Count,
            totalInserted);
    }
}
