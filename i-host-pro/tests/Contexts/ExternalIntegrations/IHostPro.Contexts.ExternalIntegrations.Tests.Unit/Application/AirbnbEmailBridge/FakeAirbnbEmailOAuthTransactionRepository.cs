using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>In-memory stand-in — no atomicity guarantees needed here, that is proven against real Postgres in the integration suite.</summary>
internal sealed class FakeAirbnbEmailOAuthTransactionRepository : IAirbnbEmailOAuthTransactionRepository
{
    private sealed record PendingRow(Guid TenantId, Guid ActorUserId, byte[] ProtectedPkceVerifier, DateTimeOffset ExpiresAtUtc, bool Consumed);

    private readonly Dictionary<string, PendingRow> _rows = [];

    public string? LastCreatedStateHash { get; private set; }
    public DateTimeOffset? LastCreatedExpiresAtUtc { get; private set; }
    public DateTimeOffset? LastCreatedAtUtc { get; private set; }

    public void CreatePending(
        Guid id, Guid tenantId, Guid actorUserId, string stateHash,
        byte[] protectedPkceVerifier, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        _rows[stateHash] = new PendingRow(tenantId, actorUserId, protectedPkceVerifier, expiresAtUtc, Consumed: false);
        LastCreatedStateHash = stateHash;
        LastCreatedAtUtc = createdAtUtc;
        LastCreatedExpiresAtUtc = expiresAtUtc;
    }

    public Task<AirbnbEmailOAuthTransactionConsumption?> ConsumeByStateHashAsync(
        string stateHash, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!_rows.TryGetValue(stateHash, out var row) || row.Consumed || row.ExpiresAtUtc <= nowUtc)
            return Task.FromResult<AirbnbEmailOAuthTransactionConsumption?>(null);

        _rows[stateHash] = row with { Consumed = true };
        return Task.FromResult<AirbnbEmailOAuthTransactionConsumption?>(
            new AirbnbEmailOAuthTransactionConsumption(row.TenantId, row.ActorUserId, row.ProtectedPkceVerifier));
    }
}
