using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// One-time, idempotent deployment-time backfill of
/// <c>dashboard.property_projection</c> from
/// <c>property_management.properties</c> (ADR-017; Fase 7, Incremento 2 —
/// Dashboard &amp; Reporting Foundation, Checkpoint 1). Same rationale and
/// mechanism as <see cref="PropertyProjectionBootstrap"/>/
/// <see cref="DashboardReservationProjectionBootstrapStep"/>.
///
/// <c>property_management.properties.status</c> stores the raw
/// <c>PropertyStatus</c> enum member name ("Draft"/"Active"/"Inactive"/
/// "Archived") — <c>LOWER(...)</c> converts it to the lowercase stable code
/// (<c>PropertyStatusCodeMapper</c>'s own convention) the real
/// <c>PropertyCreated</c>/<c>PropertyActivated</c>/... events and
/// <c>DashboardPropertyProjectionEntry.Status</c> already use.
/// <c>LastEventAtUtc</c> is seeded with the bootstrap's own execution time
/// (<c>now()</c>) for the same reason documented on
/// <see cref="DashboardReservationProjectionBootstrapStep"/>.
/// </summary>
public sealed class DashboardPropertyProjectionBootstrapStep : IProjectionBootstrapStep
{
    private readonly string _dashboardConnectionString;

    public DashboardPropertyProjectionBootstrapStep(string dashboardConnectionString) =>
        _dashboardConnectionString = dashboardConnectionString;

    public string Name => "dashboard.property_projection";

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
                INSERT INTO dashboard.property_projection
                    (tenant_id, property_id, status, last_event_at_utc)
                SELECT p.tenant_id, p.id, LOWER(p.status), now()
                FROM property_management.properties p
                ON CONFLICT (tenant_id, property_id) DO NOTHING
                """,
                connection,
                transaction);

            totalInserted += await backfillCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        log.LogInformation(
            "Dashboard property_projection backfill: {TenantCount} tenant(s) checked, {RowCount} row(s) inserted",
            tenantIds.Count,
            totalInserted);
    }
}
