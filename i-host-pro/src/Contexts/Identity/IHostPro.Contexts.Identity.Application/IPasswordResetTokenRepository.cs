namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Deliberately narrow repository for the "pre-authentication security
/// state" table (Self-Service Identity &amp; Onboarding Foundation gate) —
/// <c>password_reset_tokens</c> is NOT tenant-owned in the ordinary
/// RLS/Global-Query-Filter sense: at <c>forgot-password/complete</c> time the
/// tenant is not yet known (discovering it IS the point of
/// <see cref="ConsumeByTokenHashAsync"/>), so this table intentionally
/// carries no <c>ITenantOwned</c> query filter and no RLS policy. Mirrors
/// <c>IAirbnbEmailOAuthTransactionRepository</c> (Web OAuth architecture
/// gate) exactly. No list/get-all/query-by-tenant method exists or may be
/// added.
/// </summary>
public interface IPasswordResetTokenRepository
{
    /// <summary>
    /// Stages a new pending token row. Called only after a real account was
    /// resolved for the request — never for an unresolved tenant/email
    /// (there would be nothing to attach the row to, and doing so would leak
    /// account existence). Persistence is committed by the caller's ambient
    /// transaction.
    /// </summary>
    void CreatePending(Guid id, Guid tenantId, Guid userId, string tokenHash, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc);

    /// <summary>
    /// Atomically validates and consumes (one conditional
    /// <c>UPDATE ... RETURNING</c> — never a separate SELECT-then-UPDATE) the
    /// pending token matching <paramref name="tokenHash"/>, if it exists, has
    /// not expired, and has not already been consumed. Returns <c>null</c>
    /// for any of those failure cases — a single, deliberately
    /// undifferentiated outcome ("invalid, expired, or replayed token") —
    /// callers must never distinguish between them in any response surfaced
    /// back to the caller. Runs with NO ambient tenant context.
    /// </summary>
    Task<PasswordResetTokenConsumption?> ConsumeByTokenHashAsync(string tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

/// <summary>Trusted correlation data recovered from a successfully consumed password-reset token row.</summary>
public sealed record PasswordResetTokenConsumption(Guid TenantId, Guid UserId);
