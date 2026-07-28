using IHostPro.BuildingBlocks.Application;

namespace IHostPro.BuildingBlocks.Infrastructure.Multitenancy;

/// <inheritdoc cref="ICurrentTenantProvider"/>
public sealed class TenantContextCurrentTenantProvider : ICurrentTenantProvider
{
    private readonly ITenantContext _tenantContext;

    public TenantContextCurrentTenantProvider(ITenantContext tenantContext) => _tenantContext = tenantContext;

    public Guid TenantId => _tenantContext.TenantId
        ?? throw new TenantContextNotResolvedException();
}
