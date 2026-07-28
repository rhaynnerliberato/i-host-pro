using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Runs the Logout transactional operation, retrying a bounded number of
/// times specifically on a concurrency conflict on a refresh token's row
/// racing against a concurrent Refresh (Incremento 2 plan, Etapa 11
/// correction) — same reasoning as <see cref="IRefreshTokenExchangeExecutor"/>:
/// this retry is local to the one use case whose full effect set is known
/// and verified safe to re-run (no external effect before the Unit of
/// Work's single <c>SaveChangesAsync</c>), not a generic Unit of Work
/// behavior.
///
/// Declared here, in Application, so the future logout HTTP endpoint (and
/// any other caller in <c>IHostPro.Contexts.Identity.Api</c>) depends only
/// on this abstraction — never on the concrete Infrastructure class.
/// </summary>
public interface ILogoutExecutor
{
    Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken);
}
