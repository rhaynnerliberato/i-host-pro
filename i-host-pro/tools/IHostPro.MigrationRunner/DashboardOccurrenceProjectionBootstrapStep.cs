using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// One-time, idempotent deployment-time backfill of
/// <c>dashboard.occurrence_projection</c> from
/// <c>housekeeping.cleaning_occurrences</c> (ADR-017; Fase 7, Incremento 2 —
/// Dashboard &amp; Reporting Foundation, Checkpoint 1). Same rationale and
/// mechanism as <see cref="PropertyProjectionBootstrap"/>/
/// <see cref="DashboardReservationProjectionBootstrapStep"/>.
///
/// <c>housekeeping.cleaning_occurrences.type</c> stores the raw
/// <c>OccurrenceType</c> enum member name via <c>HasConversion&lt;string&gt;()</c>
/// — those names already exactly equal <c>OccurrenceTypeCodeMapper</c>'s
/// stable codes (Theft/Breakage/ForgottenObject/Damage/Animal/Smoking/Noise/
/// MaterialShortage), so no case conversion is applied here.
///
/// <c>dashboard.occurrence_projection</c> has no <c>last_event_at_utc</c>
/// column — occurrences are append-only (registered once, never mutated by
/// any later event; see <see cref="DashboardOccurrenceProjectionSynchronizer"/>),
/// so the out-of-order guard used on the mutable projections does not apply
/// here.
/// </summary>
public sealed class DashboardOccurrenceProjectionBootstrapStep : IProjectionBootstrapStep
{
    private readonly string _dashboardConnectionString;

    public DashboardOccurrenceProjectionBootstrapStep(string dashboardConnectionString) =>
        _dashboardConnectionString = dashboardConnectionString;

    public string Name => "dashboard.occurrence_projection";

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
                INSERT INTO dashboard.occurrence_projection
                    (tenant_id, occurrence_id, cleaning_id, type, registered_at_utc)
                SELECT o.tenant_id, o.id, o.cleaning_id, o.type, o.registered_at_utc
                FROM housekeeping.cleaning_occurrences o
                ON CONFLICT (tenant_id, occurrence_id) DO NOTHING
                """,
                connection,
                transaction);

            totalInserted += await backfillCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        log.LogInformation(
            "Dashboard occurrence_projection backfill: {TenantCount} tenant(s) checked, {RowCount} row(s) inserted",
            tenantIds.Count,
            totalInserted);
    }
}
