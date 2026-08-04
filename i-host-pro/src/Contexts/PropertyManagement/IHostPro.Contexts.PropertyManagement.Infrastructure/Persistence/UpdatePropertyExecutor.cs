using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IUpdatePropertyExecutor"/>
/// <remarks>
/// No retry loop (Checkpoint 3 plan, item 18: "sem retry automático").
/// <see cref="DbUpdateConcurrencyException"/> is caught BEFORE the generic
/// <see cref="DbUpdateException"/> clause below — required, not stylistic: it
/// is a subclass, so catch-clause order determines which one a concurrency
/// failure actually matches (mirrors Identity's <c>UpdateUserExecutor</c>
/// reasoning). Both translated failures clear the
/// <see cref="DbContext.ChangeTracker"/> and drain the event collector —
/// mirrors <c>UpdateCondominiumExecutor</c>'s own defensive cleanup.
/// </remarks>
public sealed class UpdatePropertyExecutor : IUpdatePropertyExecutor
{
    private const string CodeUniqueIndexName = "uq_properties_tenant_normalized_code";

    private static readonly Error PropertyCodeAlreadyExistsError = new(
        PropertyManagementErrorCodes.PropertyCodeAlreadyExists, PropertyManagementErrorCodes.PropertyCodeAlreadyExists);
    private static readonly Error PropertyConcurrencyConflictError = new(
        PropertyManagementErrorCodes.PropertyConcurrencyConflict, PropertyManagementErrorCodes.PropertyConcurrencyConflict);

    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;
    private readonly PropertyManagementDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public UpdatePropertyExecutor(
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
        catch (DbUpdateException ex) when (IsCodeUniqueViolation(ex))
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            return Result.Failure<PropertyResult>(PropertyCodeAlreadyExistsError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            throw;
        }
    }

    private static bool IsCodeUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CodeUniqueIndexName,
        };
}
