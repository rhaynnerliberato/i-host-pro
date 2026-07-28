using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Runs the Refresh Token exchange's transactional operation, retrying a
/// bounded number of times specifically on a concurrency conflict on the
/// presented token's row (Incremento 2 plan, Etapa 10 correction — see the
/// Infrastructure implementation's doc comment for why this retry is scoped
/// to this one use case instead of living in the shared Unit of Work).
///
/// Declared here, in Application, so the future refresh HTTP endpoint (and
/// any other caller in <c>IHostPro.Contexts.Identity.Api</c>) depends only on
/// this abstraction — never on the concrete Infrastructure class, which also
/// carries an EF Core <c>DbContext</c> dependency Application-layer callers
/// must never see directly (Architecture Principles, Section 4).
/// </summary>
public interface IRefreshTokenExchangeExecutor
{
    Task<Result<AuthTokensResult>> ExecuteAsync(
        Func<Task<Result<AuthTokensResult>>> operation, CancellationToken cancellationToken);
}
