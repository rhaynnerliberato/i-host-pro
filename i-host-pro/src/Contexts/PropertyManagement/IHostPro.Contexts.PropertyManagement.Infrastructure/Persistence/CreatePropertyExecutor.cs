using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="ICreatePropertyExecutor"/>
/// <remarks>
/// Catches the specific PostgreSQL unique-violation (<c>23505</c>) on
/// <c>uq_properties_tenant_normalized_code</c> — the exact constraint name
/// from <c>PropertyConfiguration</c> — and translates it into
/// <see cref="PropertyManagementErrorCodes.PropertyCodeAlreadyExists"/>. Any
/// OTHER <see cref="DbUpdateException"/> is deliberately left to propagate,
/// mirroring Identity's <c>CreateUserExecutor</c>. On failure, clears the
/// <see cref="PropertyManagementDbContext.ChangeTracker"/> and drains the event collector —
/// mirrors <c>UpdateCondominiumExecutor</c>'s own defensive cleanup.
/// </remarks>
public sealed class CreatePropertyExecutor : ICreatePropertyExecutor
{
    private const string CodeUniqueIndexName = "uq_properties_tenant_normalized_code";

    private static readonly Error PropertyCodeAlreadyExistsError = new(
        PropertyManagementErrorCodes.PropertyCodeAlreadyExists, PropertyManagementErrorCodes.PropertyCodeAlreadyExists);

    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;
    private readonly PropertyManagementDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public CreatePropertyExecutor(
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
