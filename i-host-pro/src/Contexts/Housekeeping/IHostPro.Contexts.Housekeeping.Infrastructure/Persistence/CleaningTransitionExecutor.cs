using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="ICleaningTransitionExecutor"/>
/// <remarks>Mirrors <c>CancelReservationExecutor</c>/<c>UpdateReservationExecutor</c>'s concurrency translation exactly.</remarks>
public sealed class CleaningTransitionExecutor : ICleaningTransitionExecutor
{
    private static readonly Error CleaningConcurrencyConflictError = new(
        HousekeepingErrorCodes.CleaningConcurrencyConflict, HousekeepingErrorCodes.CleaningConcurrencyConflict);

    private readonly IHousekeepingTransactionExecutor _transactionExecutor;
    private readonly HousekeepingDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public CleaningTransitionExecutor(
        IHousekeepingTransactionExecutor transactionExecutor,
        HousekeepingDbContext dbContext,
        IIntegrationEventCollector eventCollector)
    {
        _transactionExecutor = transactionExecutor;
        _dbContext = dbContext;
        _eventCollector = eventCollector;
    }

    public async Task<Result<CleaningResult>> ExecuteAsync(
        Func<Task<Result<CleaningResult>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            return Result.Failure<CleaningResult>(CleaningConcurrencyConflictError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            throw;
        }
    }
}
