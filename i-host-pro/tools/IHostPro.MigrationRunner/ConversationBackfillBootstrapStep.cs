using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// One-time, idempotent deployment-time backfill of
/// <c>communication.messages.conversation_id</c> (Fase 11, Checkpoint 1 —
/// Inbound Conversation Foundation) — same rationale/mechanism as
/// <see cref="GuestStayOperationBackfillBootstrapStep"/>, applied here to a
/// SAME-context migration rather than a cross-context one: both
/// <c>communication.messages</c> and the new <c>communication.conversations</c>
/// have <c>FORCE ROW LEVEL SECURITY</c>, so a plain cross-tenant
/// <c>INSERT</c>/<c>UPDATE</c> inside the EF migration itself would see zero
/// rows without <c>app.tenant_id</c> set per tenant — the same constraint
/// already solved this way for Guest Operations, never
/// BYPASSRLS/IgnoreQueryFilters.
///
/// For each tenant: creates exactly one <c>Conversation</c> per distinct
/// <c>(reservation_id, channel)</c> group already present among that
/// tenant's <c>messages</c> (idempotent via
/// <c>ON CONFLICT (tenant_id, reservation_id, channel) DO NOTHING</c>,
/// against the same unique index <see cref="ConversationConfiguration"/>
/// already declares), then points every message still carrying the
/// migration's placeholder all-zero <c>conversation_id</c> at its matching
/// Conversation. Never invents a Reservation — uses exactly the
/// <c>tenant_id</c>/<c>reservation_id</c>/<c>channel</c> values already
/// stored on each pre-existing <c>Message</c> row, even where that
/// <c>reservation_id</c> no longer resolves to a live row in
/// <c>reservations.reservations</c> (confirmed, dev database audit,
/// Checkpoint 1: 3 pre-existing messages, all orphaned Fase 9 sandbox-proof
/// artifacts) — Conversation's origin is the id itself, not a live foreign
/// key (ADR-029/mandate item 14, no cross-context FK).
/// </summary>
public sealed class ConversationBackfillBootstrapStep : IProjectionBootstrapStep
{
    private static readonly Guid PlaceholderConversationId = Guid.Empty;

    private readonly string _communicationConnectionString;

    public ConversationBackfillBootstrapStep(string communicationConnectionString) =>
        _communicationConnectionString = communicationConnectionString;

    public string Name => "communication.messages.conversation_id";

    public async Task ExecuteAsync(ILogger log, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_communicationConnectionString);
        await connection.OpenAsync(cancellationToken);

        var tenantIds = new List<Guid>();
        await using (var tenantsCommand = new NpgsqlCommand("SELECT id FROM identity.tenants", connection))
        await using (var reader = await tenantsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                tenantIds.Add(reader.GetGuid(0));
        }

        var totalConversationsInserted = 0;
        var totalMessagesBackfilled = 0;

        foreach (var tenantId in tenantIds)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var setTenantCommand = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id', $1, true)", connection, transaction))
            {
                setTenantCommand.Parameters.AddWithValue(tenantId.ToString());
                await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertConversationsCommand = new NpgsqlCommand(
                """
                INSERT INTO communication.conversations
                    (id, tenant_id, reservation_id, channel, status, created_at_utc, updated_at_utc, last_message_at_utc)
                SELECT gen_random_uuid(), m.tenant_id, m.reservation_id, m.channel, 'Active',
                       MIN(m.created_at_utc), MAX(m.created_at_utc), MAX(m.created_at_utc)
                FROM communication.messages m
                GROUP BY m.tenant_id, m.reservation_id, m.channel
                ON CONFLICT (tenant_id, reservation_id, channel) DO NOTHING
                """,
                connection,
                transaction))
            {
                totalConversationsInserted += await insertConversationsCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var backfillMessagesCommand = new NpgsqlCommand(
                """
                UPDATE communication.messages m
                SET conversation_id = c.id
                FROM communication.conversations c
                WHERE m.tenant_id = c.tenant_id
                  AND m.reservation_id = c.reservation_id
                  AND m.channel = c.channel
                  AND m.conversation_id = $1
                """,
                connection,
                transaction))
            {
                backfillMessagesCommand.Parameters.AddWithValue(PlaceholderConversationId);
                totalMessagesBackfilled += await backfillMessagesCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        log.LogInformation(
            "Communication conversation backfill: {TenantCount} tenant(s) checked, {ConversationCount} conversation(s) inserted, {MessageCount} message(s) backfilled",
            tenantIds.Count,
            totalConversationsInserted,
            totalMessagesBackfilled);
    }
}
