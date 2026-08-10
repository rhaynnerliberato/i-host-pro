using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Policies;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

/// <summary>
/// Simply invokes <c>operation</c> directly, no real transaction — these
/// unit tests exercise handler logic only; the real executor (and its
/// database-level version-conflict translation) is covered by the
/// integration test suite.
/// </summary>
internal sealed class PassThroughCreatePolicyValueVersionExecutor : ICreatePolicyValueVersionExecutor
{
    public Task<Result<PolicyValueDetailResult>> ExecuteAsync(
        Func<Task<Result<PolicyValueDetailResult>>> operation, CancellationToken cancellationToken) => operation();
}
