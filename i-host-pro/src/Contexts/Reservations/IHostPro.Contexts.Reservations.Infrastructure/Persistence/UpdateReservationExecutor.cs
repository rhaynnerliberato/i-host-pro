using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="IUpdateReservationExecutor"/>
/// <remarks>
/// No retry loop ("sem retry automático"). Mirrors
/// <c>UpdatePropertyExecutor</c>'s concurrency translation exactly — no
/// unique-constraint violation to catch here (Reservations has none).
/// </remarks>
public sealed class UpdateReservationExecutor : IUpdateReservationExecutor
{
    private static readonly Error ReservationConcurrencyConflictError = new(
        ReservationsErrorCodes.ReservationConcurrencyConflict, ReservationsErrorCodes.ReservationConcurrencyConflict);

    private readonly IReservationsTransactionExecutor _transactionExecutor;
    private readonly ReservationsDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public UpdateReservationExecutor(
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
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            return Result.Failure<ReservationResult>(ReservationConcurrencyConflictError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            throw;
        }
    }
}
