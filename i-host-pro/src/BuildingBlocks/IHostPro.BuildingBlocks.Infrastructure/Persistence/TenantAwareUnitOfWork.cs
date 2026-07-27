using System.Data;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <inheritdoc cref="ITenantAwareUnitOfWork"/>
public sealed class TenantAwareUnitOfWork : ITenantAwareUnitOfWork
{
    private readonly DbContext _dbContext;
    private readonly ITenantContext _tenantContext;

    public TenantAwareUnitOfWork(DbContext dbContext, ITenantContext tenantContext)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
    }

    public async Task<TResponse> ExecuteAsync<TResponse>(
        bool readOnly,
        Func<Task<TResponse>> operation,
        CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
            throw new NestedUnitOfWorkException();

        if (!_tenantContext.IsResolved)
            throw new TenantContextNotResolvedException();

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // PostgreSQL does not support bind parameters inside a SET command, so
        // this cannot use ExecuteSqlInterpolatedAsync — the value must be
        // embedded as a literal. This is safe because the source is a Guid
        // struct (Guid.ToString("D") only ever produces hex digits and hyphens,
        // never a character that could break out of the string literal), never
        // free-form user input.
        //
        // SET LOCAL (never a plain SET) scopes the setting to this transaction
        // only — it is automatically undone at COMMIT/ROLLBACK and can never
        // leak into the physical connection when Npgsql returns it to the pool
        // (Architecture Principles, Section 7).
        var tenantIdLiteral = _tenantContext.TenantId!.Value.ToString("D");

        // EF1002 is suppressed deliberately: ExecuteSqlAsync (the parameterized,
        // analyzer-approved overload) cannot be used here because PostgreSQL
        // rejects bind parameters inside a SET command. tenantIdLiteral is not
        // free-form input — it is Guid.ToString("D"), which only ever produces
        // hex digits and hyphens, so no value can break out of the string
        // literal.
#pragma warning disable EF1002
        await _dbContext.Database.ExecuteSqlRawAsync(
            $"SET LOCAL app.tenant_id = '{tenantIdLiteral}'",
            cancellationToken);
#pragma warning restore EF1002

        if (readOnly)
        {
            await _dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        }

        var result = await operation();
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }
}
