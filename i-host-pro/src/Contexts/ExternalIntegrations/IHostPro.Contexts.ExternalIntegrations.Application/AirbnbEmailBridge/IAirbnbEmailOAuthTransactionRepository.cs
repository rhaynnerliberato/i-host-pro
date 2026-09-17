namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Deliberately narrow repository for the Web OAuth "pre-authentication
/// security state" table (Web OAuth architecture gate, item 13-14) —
/// <c>airbnb_email_oauth_transactions</c> is NOT tenant-owned in the ordinary
/// RLS/Global-Query-Filter sense: at <c>oauth/callback</c> time the tenant is
/// not yet known (discovering it IS the point of <see cref="ConsumeByStateHashAsync"/>),
/// so this table intentionally carries no <c>ITenantOwned</c> query filter and
/// no RLS policy. Its safety comes entirely from this narrow surface — no
/// list/get-all/query-by-tenant method exists or may be added.
/// </summary>
public interface IAirbnbEmailOAuthTransactionRepository
{
    /// <summary>
    /// Stages a new pending transaction row. Called only from the
    /// authenticated <c>oauth/start</c> path, where <c>tenantId</c>/<c>actorUserId</c>
    /// come from the caller's own already-validated iHostPro JWT — never
    /// accepted as request input. Persistence is committed by the caller's
    /// ambient unit of work (mirrors every other <c>Add</c>-style repository
    /// method in this Bounded Context).
    /// </summary>
    void CreatePending(
        Guid id, Guid tenantId, Guid actorUserId, string stateHash,
        byte[] protectedPkceVerifier, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc);

    /// <summary>
    /// Atomically validates and consumes (in one round trip, one conditional
    /// UPDATE ... RETURNING — no separate SELECT-then-UPDATE) the pending
    /// transaction matching <paramref name="stateHash"/>, if it exists, has
    /// not expired, and has not already been consumed. Returns <c>null</c>
    /// for any of those failure cases (a single, deliberately
    /// undifferentiated outcome: "invalid or replayed state") — callers must
    /// never distinguish "not found" from "already consumed" from "expired"
    /// in any response surfaced back to the caller. Runs with NO ambient
    /// tenant context (the tenant context is not yet resolved at this point
    /// in the callback) — this is the one deliberate exception to "every
    /// tenant-owned read goes through <c>IAirbnbEmailUnitOfWork</c>" in this
    /// Bounded Context.
    /// </summary>
    Task<AirbnbEmailOAuthTransactionConsumption?> ConsumeByStateHashAsync(
        string stateHash, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

/// <summary>Trusted correlation data recovered from a successfully consumed OAuth transaction row.</summary>
public sealed record AirbnbEmailOAuthTransactionConsumption(Guid TenantId, Guid ActorUserId, byte[] ProtectedPkceVerifier);
