using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class WhatsAppTenantRouteRepository : IWhatsAppTenantRouteRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public WhatsAppTenantRouteRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<WhatsAppTenantRoute?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _dbContext.WhatsAppTenantRoutes.FirstOrDefaultAsync(r => r.TenantId == tenantId, cancellationToken);

    public Task<WhatsAppTenantRoute?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken) =>
        _dbContext.WhatsAppTenantRoutes.FirstOrDefaultAsync(r => r.PhoneNumberId == phoneNumberId, cancellationToken);

    public void Add(WhatsAppTenantRoute route) => _dbContext.WhatsAppTenantRoutes.Add(route);

    public void Remove(WhatsAppTenantRoute route) => _dbContext.WhatsAppTenantRoutes.Remove(route);
}
