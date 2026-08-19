using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class WhatsAppIntegrationRepository : IWhatsAppIntegrationRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public WhatsAppIntegrationRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<WhatsAppIntegration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public Task<WhatsAppIntegration?> GetForCurrentTenantAsync(CancellationToken cancellationToken) =>
        _dbContext.WhatsAppIntegrations.FirstOrDefaultAsync(cancellationToken);

    public void Add(WhatsAppIntegration aggregate) => _dbContext.WhatsAppIntegrations.Add(aggregate);

    public void Update(WhatsAppIntegration aggregate) => _dbContext.WhatsAppIntegrations.Update(aggregate);

    public void Remove(WhatsAppIntegration aggregate) => _dbContext.WhatsAppIntegrations.Remove(aggregate);
}
