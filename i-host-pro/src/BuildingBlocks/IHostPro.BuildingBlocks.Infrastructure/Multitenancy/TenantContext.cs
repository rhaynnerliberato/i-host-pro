namespace IHostPro.BuildingBlocks.Infrastructure.Multitenancy;

/// <summary>
/// Default, scoped (per-request / per-message) implementation of <see cref="ITenantContext"/>.
/// Populated once per request by an authentication/authorization middleware in
/// IHostPro.Api, or once per consumed message by <see cref="TenantResolutionMiddleware"/> in
/// IHostPro.Worker — never resolved by guessing or by a default fallback tenant.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private Guid? _tenantId;

    public Guid? TenantId => _tenantId;

    public bool IsResolved => _tenantId.HasValue;

    public void SetTenant(Guid tenantId)
    {
        if (_tenantId.HasValue && _tenantId.Value != tenantId)
            throw new InvalidOperationException("Tenant context has already been resolved for this scope.");

        _tenantId = tenantId;
    }
}
