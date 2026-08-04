using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IUnlinkPropertyOwnerExecutor"/>
/// <remarks>
/// <see cref="DbUpdateConcurrencyException"/> here means a concurrent
/// removal of the SAME link already committed first (the DELETE affected
/// zero rows) — translated to
/// <see cref="PropertyManagementErrorCodes.PropertyOwnerNotLinked"/> (404),
/// deliberately NOT <see cref="PropertyManagementErrorCodes.PropertyConcurrencyConflict"/>
/// (409): <c>property_owners</c> has no version token, and "the loser sees
/// not-found" is the approved semantics for this specific race (Checkpoint 5
/// plan, item 8/13) — distinct from every other executor in this Bounded
/// Context. No bounded retry.
/// </remarks>
public sealed class UnlinkPropertyOwnerExecutor : IUnlinkPropertyOwnerExecutor
{
    private static readonly Error PropertyOwnerNotLinkedError = new(
        PropertyManagementErrorCodes.PropertyOwnerNotLinked, PropertyManagementErrorCodes.PropertyOwnerNotLinked);

    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;
    private readonly PropertyManagementDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public UnlinkPropertyOwnerExecutor(
        IPropertyManagementTransactionExecutor transactionExecutor,
        PropertyManagementDbContext dbContext,
        IIntegrationEventCollector eventCollector)
    {
        _transactionExecutor = transactionExecutor;
        _dbContext = dbContext;
        _eventCollector = eventCollector;
    }

    public async Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            return Result.Failure(PropertyOwnerNotLinkedError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            throw;
        }
    }
}
