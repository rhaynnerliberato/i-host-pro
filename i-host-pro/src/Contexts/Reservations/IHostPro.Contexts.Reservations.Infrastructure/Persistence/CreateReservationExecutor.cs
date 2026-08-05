using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="ICreateReservationExecutor"/>
/// <remarks>
/// Unlike <c>CreatePropertyExecutor</c>, there is no database-level
/// uniqueness constraint to translate here — the date-conflict rule is
/// entirely an application-level check under
/// <see cref="IReservationConflictGuard"/>'s advisory lock, returned as an
/// ordinary <c>Result.Failure</c> from within <c>operation</c> itself, never
/// as a caught exception. The catch-all cleanup below still mirrors every
/// other executor in this codebase, as a safety net for any unexpected
/// failure.
/// </remarks>
public sealed class CreateReservationExecutor : ICreateReservationExecutor
{
    private readonly IReservationsTransactionExecutor _transactionExecutor;
    private readonly ReservationsDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public CreateReservationExecutor(
        IReservationsTransactionExecutor transactionExecutor,
        ReservationsDbContext dbContext,
        IIntegrationEventCollector eventCollector)
    {
        _transactionExecutor = transactionExecutor;
        _dbContext = dbContext;
        _eventCollector = eventCollector;
    }

    public async Task<Result<ReservationResult>> ExecuteAsync(
        Func<Task<Result<ReservationResult>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            throw;
        }
    }
}
