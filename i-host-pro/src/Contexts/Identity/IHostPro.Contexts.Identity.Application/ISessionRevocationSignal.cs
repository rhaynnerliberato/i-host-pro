namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Per-request (Scoped) collector of "this session was just revoked" facts,
/// staged by a handler while inside its transaction and drained by its
/// executor (<c>LogoutExecutor</c>/<c>RefreshTokenExchangeExecutor</c>) AFTER
/// the transaction has committed, to decide what to write to
/// <see cref="ISessionRevocationCache"/> (Incremento 2 plan, Etapa 12).
///
/// This exists so the post-commit cache write requires no change to the
/// handlers' <c>Handle()</c> return type (still plain <c>Result</c>/
/// <c>Result&lt;T&gt;</c>, per <c>ICommandHandler</c>) and no change to the
/// executors' public <c>ExecuteAsync</c> signature — only their internal
/// dependencies grow. <see cref="LogoutCommandHandler"/> marks a revocation
/// only on the path where it actually calls <c>Session.Revoke</c> — never on
/// an idempotent no-op (nonexistent/foreign/already-revoked session) — so a
/// repeated logout call naturally never re-writes the cache entry.
/// </summary>
public interface ISessionRevocationSignal
{
    void MarkRevoked(Guid tenantId, Guid sessionId);

    /// <summary>Returns every revocation staged since the last <see cref="Drain"/> call, and clears the collector.</summary>
    IReadOnlyCollection<(Guid TenantId, Guid SessionId)> Drain();
}
