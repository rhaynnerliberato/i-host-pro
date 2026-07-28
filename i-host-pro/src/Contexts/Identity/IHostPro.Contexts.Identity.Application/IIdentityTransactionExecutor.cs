namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Runs a Login/Refresh/Logout handler inside the tenant-aware RLS
/// transaction (same mechanics <c>ITenantAwareUnitOfWork</c> uses) and, unlike
/// it, also flushes any Integration Event the handler staged on
/// <see cref="IIntegrationEventCollector"/> to Identity's own durable outbox
/// atomically with the domain change, before committing (Incremento 2 plan,
/// Etapa 15A). Every auth command is a write, so — unlike <c>ITenantAwareUnitOfWork</c>
/// — there is no <c>readOnly</c> parameter.
///
/// A <c>Result.Failure</c> returned by <paramref name="operation"/> is not by
/// itself a rollback signal: a rejected login can still need to persist a
/// lockout increment, an audit entry and a <c>LoginFailed</c>/<c>AccountLockedOut</c>
/// event. Only an exception propagating out of <paramref name="operation"/>
/// or out of the transactional/outbox persistence itself causes a rollback.
///
/// Declared here, in Application, so <c>LoginCommandHandler</c>'s pipeline
/// behavior and <see cref="IRefreshTokenExchangeExecutor"/>/<see cref="ILogoutExecutor"/>
/// depend only on this abstraction — never on the concrete Infrastructure
/// class, which also carries EF Core/Wolverine dependencies Application-layer
/// callers must never see directly (Architecture Principles, Section 4/11).
/// </summary>
public interface IIdentityTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
