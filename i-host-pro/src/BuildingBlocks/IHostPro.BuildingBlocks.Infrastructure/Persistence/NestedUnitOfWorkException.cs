namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Thrown when <see cref="ITenantAwareUnitOfWork"/> is asked to open a
/// transaction on a <see cref="Microsoft.EntityFrameworkCore.DbContext"/> that
/// already has one active. The platform's pipeline design guarantees exactly one
/// TenantTransactionBehavior/TenantBootstrapBehavior execution wraps exactly one
/// handler call per scoped DbContext (Architecture Principles, Section 7) — this
/// exception signals that invariant was violated, which always indicates a
/// programming error (e.g. a handler manually opening its own transaction, or a
/// pipeline misconfiguration), never an expected runtime condition. It is a
/// dedicated infrastructure exception rather than a generic BCL exception so
/// callers/tests can distinguish this specific architectural violation from any
/// other <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class NestedUnitOfWorkException : Exception
{
    public NestedUnitOfWorkException()
        : base("A transaction is already active for this DbContext. Nested execution of ITenantAwareUnitOfWork is not supported — each unit of work must wrap exactly one handler call.")
    {
    }
}
