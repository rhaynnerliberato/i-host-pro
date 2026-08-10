using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// Wraps <see cref="CreatePolicyValueVersionCommand"/>'s write in this
/// context's own transactional boundary, translating a caught
/// <c>DbUpdateException</c> (the partial unique index's own last-resort
/// defense against a genuine concurrent write racing past the handler's own
/// expected-version pre-check) into
/// <see cref="Errors.PolicyErrorCodes.VersionConflict"/> — mirrors
/// <c>IUpdateReservationExecutor</c>'s concurrency-translation shape. No
/// retry loop.
/// </summary>
public interface ICreatePolicyValueVersionExecutor
{
    Task<Result<PolicyValueDetailResult>> ExecuteAsync(
        Func<Task<Result<PolicyValueDetailResult>>> operation, CancellationToken cancellationToken);
}
