using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// One-time, idempotent deployment-time backfill of
/// <c>dashboard.reservation_projection</c> from <c>reservations.reservations</c>
/// (ADR-017; Fase 7, Incremento 2 — Dashboard &amp; Reporting Foundation,
/// Checkpoint 1) — same rationale, mechanism and constraints as
/// <see cref="PropertyProjectionBootstrap"/>: a Reservation created before
/// Dashboard's own consumer queue existed would otherwise be permanently
/// invisible to Dashboard's projection (RabbitMQ never replays history to a
/// newly-bound queue).
///
/// <c>reservations.reservations.status</c> stores the raw
/// <c>ReservationStatus</c> enum member name ("Confirmed"/"Cancelled") —
/// <c>LOWER(...)</c> converts it to the lowercase stable code
/// (<c>ReservationStatusCodeMapper</c>'s own convention) the real
/// <c>ReservationCreated</c>/<c>ReservationCancelled</c> events and
/// <c>DashboardReservationProjectionEntry.Status</c> already use.
/// <c>LastEventAtUtc</c> is seeded with the bootstrap's own execution time
/// (<c>now()</c>), never a reconstructed historical timestamp — this is
/// deliberately the safest choice: any genuine event arriving after
/// bootstrap always has a Timestamp &gt;= this seed, so the out-of-order
/// guard in <c>DashboardReservationProjectionSynchronizer</c> never rejects
/// real, subsequent updates.
/// </summary>
public sealed class DashboardReservationProjectionBootstrapStep : IProjectionBootstrapStep
{
    private readonly string _dashboardConnectionString;

    public DashboardReservationProjectionBootstrapStep(string dashboardConnectionString) =>
        _dashboardConnectionString = dashboardConnectionString;

    public string Name => "dashboard.reservation_projection";

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
                INSERT INTO dashboard.reservation_projection
                    (tenant_id, reservation_id, property_id, check_in_at, check_out_at, status, last_event_at_utc)
                SELECT r.tenant_id, r.id, r.property_id, r.check_in_at, r.check_out_at, LOWER(r.status), now()
                FROM reservations.reservations r
                ON CONFLICT (tenant_id, reservation_id) DO NOTHING
                """,
                connection,
                transaction);

            totalInserted += await backfillCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        log.LogInformation(
            "Dashboard reservation_projection backfill: {TenantCount} tenant(s) checked, {RowCount} row(s) inserted",
            tenantIds.Count,
            totalInserted);
    }
}
