using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// One-time, idempotent deployment-time backfill of
/// <c>guest_operations.guest_stay_operations</c> from
/// <c>reservations.reservations</c> (ADR-017; Fase 10, Checkpoint 2 —
/// Check-in/Checkout Core, Existing Reservation Upgrade Strategy) — same
/// rationale, mechanism and constraints as
/// <see cref="DashboardReservationProjectionBootstrapStep"/>: a Reservation
/// created before Guest Operations' own choreography consumer
/// (<c>ReservationCreatedGuestStayInitializer</c>) was ever bound to
/// <c>guestoperations.reservation-created-trigger</c> would otherwise be
/// permanently invisible to Guest Operations (RabbitMQ never replays history
/// to a newly-bound queue).
///
/// Only <c>Confirmed</c> pre-existing Reservations are backfilled, each into
/// a new <c>Active</c> <c>GuestStayOperation</c> — mirroring exactly what the
/// real choreography would have created had it existed at the time.
/// <c>Cancelled</c> pre-existing Reservations are deliberately skipped: a
/// Cancelled Reservation can never check in, so a
/// <c>GuestStayOperation</c> for it would be pure dead state, and
/// fabricating one to represent a historical cancellation would invent a
/// business concept nothing in this checkpoint defines. A pre-existing
/// <c>Closed</c> Reservation is also outside this backfill's scope (the
/// <c>WHERE r.status = 'Confirmed'</c> filter excludes it): reaching
/// <c>Closed</c> requires a real prior checkout through Guest Operations,
/// which is only possible if a <c>GuestStayOperation</c> already existed —
/// a <c>Closed</c> Reservation missing one is a genuine data anomaly to
/// investigate, never something this backfill should paper over by
/// fabricating a fictitious history.
///
/// Idempotent via <c>ON CONFLICT (tenant_id, reservation_id) DO NOTHING</c>
/// against the same unique index <see cref="GuestStayOperationConfiguration"/>
/// already declares (defense-in-depth, never the only guarantee) — running
/// this step again inserts nothing new and never regresses an
/// already-CheckedIn/CheckedOut operation back to Active.
///
/// <c>CreatedAtUtc</c>/<c>UpdatedAtUtc</c> are seeded with the bootstrap's
/// own execution time (<c>now()</c>), never a reconstructed historical
/// timestamp — mirrors <see cref="DashboardReservationProjectionBootstrapStep"/>'s
/// own <c>LastEventAtUtc</c> convention exactly.
/// </summary>
public sealed class GuestStayOperationBackfillBootstrapStep : IProjectionBootstrapStep
{
    private readonly string _guestOperationsConnectionString;

    public GuestStayOperationBackfillBootstrapStep(string guestOperationsConnectionString) =>
        _guestOperationsConnectionString = guestOperationsConnectionString;

    public string Name => "guest_operations.guest_stay_operations";

    public async Task ExecuteAsync(ILogger log, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_guestOperationsConnectionString);
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
                INSERT INTO guest_operations.guest_stay_operations
                    (id, tenant_id, reservation_id, property_id, status, checked_in_at_utc, checked_out_at_utc, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), r.tenant_id, r.id, r.property_id, 'Active', NULL, NULL, now(), now()
                FROM reservations.reservations r
                WHERE r.status = 'Confirmed'
                ON CONFLICT (tenant_id, reservation_id) DO NOTHING
                """,
                connection,
                transaction);

            totalInserted += await backfillCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        log.LogInformation(
            "Guest Operations guest_stay_operations backfill: {TenantCount} tenant(s) checked, {RowCount} row(s) inserted " +
            "(Confirmed pre-existing Reservations only; Cancelled/Closed deliberately skipped)",
            tenantIds.Count,
            totalInserted);
    }
}
