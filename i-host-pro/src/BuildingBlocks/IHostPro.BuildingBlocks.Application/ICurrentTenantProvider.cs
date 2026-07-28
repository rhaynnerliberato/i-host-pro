namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Read-only access to the tenant already resolved for the current
/// request/message — for Application-layer code (e.g. command handlers)
/// that needs the tenant id to construct a new tenant-owned aggregate,
/// without depending on <c>BuildingBlocks.Infrastructure</c>'s
/// <c>ITenantContext</c>, which Application is not permitted to reference
/// (Architecture Principles, Section 4). Implemented in
/// <c>BuildingBlocks.Infrastructure</c> by adapting the existing
/// <c>ITenantContext</c> (Incremento 2 plan, Etapa 9) — deliberately a
/// separate, additive interface rather than moving <c>ITenantContext</c>
/// itself, to avoid touching the Incremento 1 files that already depend on
/// its current location.
///
/// <c>TenantBootstrapBehavior</c>/<c>TenantTransactionBehavior</c> guarantee
/// the tenant is already resolved before any handler runs, so
/// <see cref="TenantId"/> is always safe to read from within one.
/// </summary>
public interface ICurrentTenantProvider
{
    Guid TenantId { get; }
}
