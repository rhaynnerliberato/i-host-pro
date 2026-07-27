namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Owns the transactional boundary of a single Command/Query execution for a
/// tenant-scoped <see cref="Microsoft.EntityFrameworkCore.DbContext"/>: opens a
/// transaction, sets the PostgreSQL session variable consumed by every
/// Row-Level Security policy (<c>app.tenant_id</c>) as the first statement,
/// runs the operation, commits, and guarantees rollback on failure via
/// <c>await using</c>. `SET LOCAL` (never plain `SET`) is used so the tenant
/// setting can never leak into a pooled connection beyond this transaction
/// (Architecture Principles, Section 7).
/// </summary>
public interface ITenantAwareUnitOfWork
{
    /// <param name="readOnly">
    /// When true, the transaction is additionally opened as
    /// `SET TRANSACTION READ ONLY` — PostgreSQL itself then rejects any write
    /// attempted by <paramref name="operation"/>, catching an accidental
    /// Command/Query separation violation at the database level.
    /// </param>
    /// <param name="operation">The handler execution to run inside the transaction.</param>
    Task<TResponse> ExecuteAsync<TResponse>(
        bool readOnly,
        Func<Task<TResponse>> operation,
        CancellationToken cancellationToken);
}
