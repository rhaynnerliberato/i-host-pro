using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// One-time, idempotent deployment/data-migration concern (Fase 7, Incremento
/// 1, Checkpoint 3) — deliberately NOT a Housekeeping.Application/Domain
/// runtime dependency and NOT a permanent cross-context read.
/// <c>CreateCleaningCommandHandler</c> still only ever reads its own local
/// <c>IPropertyReferenceProjectionReader</c>; this class never touches that
/// runtime path.
///
/// Backfills <c>housekeeping.property_projection</c> for properties that
/// existed in <c>property_management.properties</c> BEFORE Housekeeping's
/// <c>PropertyCreated</c>/<c>PropertyActivated</c> consumer started listening
/// (commit f08e8d7). RabbitMQ topic exchanges never replay history to a
/// newly-bound queue, so any environment where Housekeeping's projection
/// consumer joined after PropertyManagement already had data reproduces
/// <c>CreateCleaning</c>'s <c>property_not_found</c> for those properties
/// forever, with no mechanism to self-heal at runtime (Fase 7, Checkpoint 2
/// homologação, §5.11).
///
/// Runs inside <c>IHostPro.MigrationRunner</c>, reusing the same
/// <c>ihostpro_migrator</c> connection already used for schema migrations.
/// That role deliberately has no <c>BYPASSRLS</c> (Architecture Principles,
/// Section 7) and both <c>property_management.properties</c> and
/// <c>housekeeping.property_projection</c> have <c>FORCE ROW LEVEL
/// SECURITY</c> — so this respects RLS the same way every other tenant-scoped
/// write in this codebase does (<c>TenantAwareTransactionScope</c>): looping
/// per tenant and setting <c>app.tenant_id</c> for the duration of one
/// transaction, never by disabling row security. Both schemas live in the
/// same physical PostgreSQL database, so a single connection can read
/// PropertyManagement and write Housekeeping's projection within that same
/// transaction.
///
/// Idempotent by construction: <c>PropertyProjectionEntry</c>'s primary key is
/// <c>(TenantId, PropertyId)</c> (<c>PropertyProjectionEntryConfiguration.cs</c>),
/// so <c>ON CONFLICT DO NOTHING</c> guarantees running this again never
/// duplicates or regresses an existing row — a row already present was either
/// backfilled correctly before, or is being kept current by the real-time
/// <c>PropertyProjectionSynchronizer</c>, which this step never overwrites.
/// </summary>
public static class PropertyProjectionBootstrap
{
    public static async Task RunAsync(string connectionString, ILogger log, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // identity.tenants is a platform-level catalog, deliberately NOT
        // RLS-protected (Identity's own InitialCreate migration comment) —
        // reading it needs no tenant context.
        var tenantIds = new List<Guid>();
        await using (var tenantsCommand = new NpgsqlCommand("SELECT id FROM identity.tenants", connection))
        await using (var reader = await tenantsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tenantIds.Add(reader.GetGuid(0));
            }
        }

        var totalInserted = 0;
        foreach (var tenantId in tenantIds)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // set_config(..., true) is the parameterizable equivalent of
            // SET LOCAL (PostgreSQL rejects bind parameters inside the SET
            // statement itself, but not inside a regular function call) —
            // same mechanism ReservationCommandHandlerTests' own
            // SetTenantAsync helper already uses; transaction-scoped
            // identically to SET LOCAL.
            await using (var setTenantCommand = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id', $1, true)", connection, transaction))
            {
                setTenantCommand.Parameters.AddWithValue(tenantId.ToString());
                await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            // RLS (FORCE ROW LEVEL SECURITY on both tables) scopes the SELECT
            // and the INSERT to app.tenant_id set above — no explicit
            // tenant_id predicate needed, same reliance every EF Core global
            // query filter in this codebase already has on RLS doing the
            // filtering. status = 'Active' mirrors exactly what
            // PropertyActivatedHandler/PropertyProjectionSynchronizer would
            // have set IsActive to had the real event been consumed.
            await using var backfillCommand = new NpgsqlCommand(
                """
                INSERT INTO housekeeping.property_projection (tenant_id, property_id, is_active)
                SELECT p.tenant_id, p.id, (p.status = 'Active')
                FROM property_management.properties p
                ON CONFLICT (tenant_id, property_id) DO NOTHING
                """,
                connection,
                transaction);

            totalInserted += await backfillCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        log.LogInformation(
            "Property projection backfill: {TenantCount} tenant(s) checked, {RowCount} row(s) inserted",
            tenantIds.Count,
            totalInserted);
    }
}
