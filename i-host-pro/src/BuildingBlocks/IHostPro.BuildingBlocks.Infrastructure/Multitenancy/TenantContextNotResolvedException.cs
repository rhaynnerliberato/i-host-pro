namespace IHostPro.BuildingBlocks.Infrastructure.Multitenancy;

/// <summary>
/// Thrown when a tenant-scoped operation attempts to run before
/// <see cref="ITenantContext"/> has been resolved. Every request other than an
/// explicit <c>IHostPro.BuildingBlocks.Application.IBootstrapRequest</c> requires
/// the tenant to already be known (from an authenticated JWT claim or, for the
/// Worker, from the consumed message envelope) before any handler runs — this is
/// a fail-closed guard against accidentally running a query with no tenant
/// isolation applied. A dedicated exception type, not a generic BCL one, so this
/// specific wiring bug is distinguishable from any other invalid-operation case.
/// </summary>
public sealed class TenantContextNotResolvedException : Exception
{
    public TenantContextNotResolvedException()
        : base("The tenant context has not been resolved. Tenant-scoped requests must resolve ITenantContext before reaching TenantTransactionBehavior.")
    {
    }
}
