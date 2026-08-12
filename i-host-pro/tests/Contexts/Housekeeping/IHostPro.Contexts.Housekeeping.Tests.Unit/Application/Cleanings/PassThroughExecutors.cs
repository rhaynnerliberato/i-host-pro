using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

/// <summary>
/// Test doubles that simply invoke <c>operation</c> directly, with no real
/// transaction/outbox/concurrency-conflict handling — these unit tests
/// exercise handler logic only; the real executors (including the
/// DbUpdateConcurrencyException -> CleaningConcurrencyConflict translation)
/// are covered by the integration test suite (a real PostgreSQL optimistic
/// concurrency conflict cannot be meaningfully faked here).
/// </summary>
internal sealed class PassThroughHousekeepingTransactionExecutor : IHousekeepingTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken) => operation();
}

internal sealed class PassThroughCleaningTransitionExecutor : ICleaningTransitionExecutor
{
    public Task<Result<CleaningResult>> ExecuteAsync(
        Func<Task<Result<CleaningResult>>> operation, CancellationToken cancellationToken) => operation();
}
