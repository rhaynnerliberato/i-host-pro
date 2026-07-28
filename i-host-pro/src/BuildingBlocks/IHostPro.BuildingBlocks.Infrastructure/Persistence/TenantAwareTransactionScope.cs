using System.Data;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Opens the RLS-protected transactional boundary shared by every tenant-aware
/// execution primitive: begins the transaction, sets the PostgreSQL session
/// variable every Row-Level Security policy consumes (<c>app.tenant_id</c>) as
/// the first statement, and optionally marks the transaction read-only.
/// Extracted from <see cref="TenantAwareUnitOfWork"/> (Incremento 2 plan, Etapa
/// 15A) so <see cref="IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityOutboxTransactionExecutor"/>
/// can reuse the exact same mechanics without duplicating them — the caller
/// remains responsible for running its operation and for committing/rolling
/// back (via disposal) the returned transaction, since what happens between
/// open and commit differs (plain <c>SaveChangesAsync</c> for the shared Unit
/// of Work vs. <c>IDbContextOutbox.SaveChangesAndFlushMessagesAsync</c> for the
/// Identity outbox executor). Public (not internal) specifically so other
/// Bounded Contexts' Infrastructure projects can reuse it the same way.
/// </summary>
public static class TenantAwareTransactionScope
{
    public static async Task<IDbContextTransaction> BeginAsync(
        DbContext dbContext,
        ITenantContext tenantContext,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
            throw new NestedUnitOfWorkException();

        if (!tenantContext.IsResolved)
            throw new TenantContextNotResolvedException();

        var transaction = await dbContext.Database
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
        var tenantIdLiteral = tenantContext.TenantId!.Value.ToString("D");

        // EF1002 is suppressed deliberately: ExecuteSqlAsync (the parameterized,
        // analyzer-approved overload) cannot be used here because PostgreSQL
        // rejects bind parameters inside a SET command. tenantIdLiteral is not
        // free-form input — it is Guid.ToString("D"), which only ever produces
        // hex digits and hyphens, so no value can break out of the string
        // literal.
#pragma warning disable EF1002
        await dbContext.Database.ExecuteSqlRawAsync(
            $"SET LOCAL app.tenant_id = '{tenantIdLiteral}'",
            cancellationToken);
#pragma warning restore EF1002

        if (readOnly)
        {
            await dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        }

        return transaction;
    }
}
