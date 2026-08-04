using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Wraps <see cref="IPropertyManagementTransactionExecutor"/> for
/// <c>UnlinkPropertyOwnerCommand</c> — unlike every other executor in this
/// Bounded Context, a caught <c>DbUpdateConcurrencyException</c> here
/// translates to <see cref="Errors.PropertyManagementErrorCodes.PropertyOwnerNotLinked"/>
/// (404), never <see cref="Errors.PropertyManagementErrorCodes.PropertyConcurrencyConflict"/>
/// (409) — <c>property_owners</c> carries no version token of its own, and
/// the approved semantics for two concurrent removals of the SAME link is
/// "the loser sees not-found" (Checkpoint 5 plan, item 8/13), not a genuine
/// optimistic-concurrency conflict a caller could usefully retry against
/// newer data. No bounded retry.
/// </summary>
public interface IUnlinkPropertyOwnerExecutor
{
    Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken);
}
