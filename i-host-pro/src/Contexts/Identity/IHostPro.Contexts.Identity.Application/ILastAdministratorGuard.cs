namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Protects a tenant from ever being left without an active Administrator
/// (Incremento 3, Checkpoint 6) — consulted only when removing the
/// <c>ADMIN</c> role specifically. Framework-neutral: the PostgreSQL
/// advisory-lock mechanics that make this safe under real concurrent
/// removals live exclusively in the Infrastructure implementation.
/// </summary>
public interface ILastAdministratorGuard
{
    /// <summary>
    /// Acquires a per-tenant, transaction-scoped lock and then answers
    /// whether at least one OTHER active Administrator besides
    /// <paramref name="userId"/> exists in <paramref name="tenantId"/> — i.e.
    /// whether removing userId's own <c>ADMIN</c> role would still leave the
    /// tenant with an Administrator. Must be called from within the same
    /// transaction that will go on to remove the role: the lock, held until
    /// that transaction commits or rolls back, is what keeps this answer
    /// valid against a second, concurrently racing removal for the same
    /// tenant (Incremento 3, Checkpoint 6, Section 5 — "não usar apenas uma
    /// consulta anterior à transação").
    /// </summary>
    Task<bool> AnotherActiveAdministratorRemainsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}
