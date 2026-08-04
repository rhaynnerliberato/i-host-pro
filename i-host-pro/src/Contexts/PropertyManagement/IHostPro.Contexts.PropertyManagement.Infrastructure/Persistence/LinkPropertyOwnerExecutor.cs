using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="ILinkPropertyOwnerExecutor"/>
/// <remarks>
/// Catches the specific PostgreSQL unique-violation (<c>23505</c>) on
/// <c>uq_property_owners_tenant_property_owner</c> — the exact constraint
/// name from <c>PropertyOwnerLinkConfiguration</c> — and translates it into
/// <see cref="PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked"/>. Any
/// OTHER <see cref="DbUpdateException"/> is deliberately left to propagate,
/// mirroring <c>CreatePropertyExecutor</c>.
/// </remarks>
public sealed class LinkPropertyOwnerExecutor : ILinkPropertyOwnerExecutor
{
    private const string OwnerUniqueIndexName = "uq_property_owners_tenant_property_owner";

    private static readonly Error PropertyOwnerAlreadyLinkedError = new(
        PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked, PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked);

    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;
    private readonly PropertyManagementDbContext _dbContext;
    private readonly IIntegrationEventCollector _eventCollector;

    public LinkPropertyOwnerExecutor(
        IPropertyManagementTransactionExecutor transactionExecutor,
        PropertyManagementDbContext dbContext,
        IIntegrationEventCollector eventCollector)
    {
        _transactionExecutor = transactionExecutor;
        _dbContext = dbContext;
        _eventCollector = eventCollector;
    }

    public async Task<Result<PropertyOwnerResult>> ExecuteAsync(
        Func<Task<Result<PropertyOwnerResult>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsOwnerUniqueViolation(ex))
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            return Result.Failure<PropertyOwnerResult>(PropertyOwnerAlreadyLinkedError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _eventCollector.Drain();

            throw;
        }
    }

    private static bool IsOwnerUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: OwnerUniqueIndexName,
        };
}
