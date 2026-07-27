namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Marks a Command/Query that must resolve its own tenant before any tenant-scoped
/// data is touched, because the caller has not authenticated yet (e.g. login,
/// refresh token exchange) and therefore carries no trustworthy tenant claim.
///
/// RESERVED EXCLUSIVELY FOR AUTHENTICATION BOOTSTRAP USE CASES. It is not a
/// general-purpose alternative to the normal tenant resolution pipeline (JWT
/// claim -> ITenantContext, enforced by
/// IHostPro.BuildingBlocks.Infrastructure.Persistence.TenantTransactionBehavior).
/// Using it for any other use case requires a new architectural decision — see
/// IHostPro.BuildingBlocks.Infrastructure.Persistence.TenantBootstrapBehavior for
/// the full rationale and restriction.
/// </summary>
public interface IBootstrapRequest
{
}

/// <summary>
/// Resolves the tenant for a specific <see cref="IBootstrapRequest"/> type. Each
/// Bounded Context that defines a bootstrap request (Identity &amp; Access today)
/// provides its own implementation, restricted to the minimal, non-tenant-owned
/// data needed to identify the tenant — it must never query a tenant-owned table
/// (Architecture Principles, Section 7).
/// </summary>
public interface ITenantBootstrapResolver<in TRequest>
    where TRequest : IBootstrapRequest
{
    Task<Guid?> ResolveTenantAsync(TRequest request, CancellationToken cancellationToken);
}
