using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IUpdateCondominiumExecutor"/>
/// <remarks>
/// No retry loop (Checkpoint 2 plan, item 9: "sem retry automático"). On
/// <see cref="DbUpdateConcurrencyException"/>: clears the
/// <see cref="PropertyManagementDbContext.ChangeTracker"/>, drains the event collector, and
/// returns the translated failure instead of re-throwing — mirrors
/// <c>ChangeOwnPasswordExecutor</c>'s shape (minus the session-revocation
/// side channel, which Condominium has none of). A second, broader
/// <c>catch</c> runs the same cleanup for any OTHER exception before
/// re-throwing.
/// </remarks>
public sealed class UpdateCondominiumExecutor : IUpdateCondominiumExecutor
{
    private static readonly Error CondominiumConcurrencyConflictError = new(
        PropertyManagementErrorCodes.CondominiumConcurrencyConflict, PropertyManagementErrorCodes.CondominiumConcurrencyConflict);

    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;
    private readonly PropertyManagementDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public UpdateCondominiumExecutor(
        IPropertyManagementTransactionExecutor transactionExecutor,
        PropertyManagementDbContext dbContext,
        IIntegrationEventCollector eventCollector)
    {
        _transactionExecutor = transactionExecutor;
        _dbContext = dbContext;
        _eventCollector = eventCollector;
    }

    public async Task<Result<CondominiumResult>> ExecuteAsync(
        Func<Task<Result<CondominiumResult>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            return Result.Failure<CondominiumResult>(CondominiumConcurrencyConflictError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            throw;
        }
    }
}
