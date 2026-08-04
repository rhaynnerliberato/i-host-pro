using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="ILifecyclePropertyExecutor"/>
/// <remarks>
/// No retry loop (Checkpoint 4 plan, item 12: "não repetir automaticamente").
/// Unlike <see cref="UpdatePropertyExecutor"/>, no code-uniqueness catch:
/// none of the three lifecycle transitions touch <c>Code</c>, so a unique-
/// constraint violation can never legitimately occur here — only
/// <see cref="DbUpdateConcurrencyException"/> is translated. Mirrors
/// <c>UpdateCondominiumExecutor</c>'s defensive cleanup shape.
/// </remarks>
public sealed class LifecyclePropertyExecutor : ILifecyclePropertyExecutor
{
    private static readonly Error PropertyConcurrencyConflictError = new(
        PropertyManagementErrorCodes.PropertyConcurrencyConflict, PropertyManagementErrorCodes.PropertyConcurrencyConflict);

    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;
    private readonly PropertyManagementDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public LifecyclePropertyExecutor(
        IPropertyManagementTransactionExecutor transactionExecutor,
        PropertyManagementDbContext dbContext,
        IIntegrationEventCollector eventCollector)
    {
        _transactionExecutor = transactionExecutor;
        _dbContext = dbContext;
        _eventCollector = eventCollector;
    }

    public async Task<Result<PropertyResult>> ExecuteAsync(
        Func<Task<Result<PropertyResult>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            return Result.Failure<PropertyResult>(PropertyConcurrencyConflictError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            throw;
        }
    }
}
