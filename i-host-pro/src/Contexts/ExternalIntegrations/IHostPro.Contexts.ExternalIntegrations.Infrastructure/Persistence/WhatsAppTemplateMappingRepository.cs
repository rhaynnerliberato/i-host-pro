using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class WhatsAppTemplateMappingRepository : IWhatsAppTemplateMappingRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public WhatsAppTemplateMappingRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<WhatsAppTemplateMapping?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.WhatsAppTemplateMappings.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<WhatsAppTemplateMapping?> GetForCurrentTenantByTemplateKeyAsync(string templateKey, CancellationToken cancellationToken) =>
        _dbContext.WhatsAppTemplateMappings.FirstOrDefaultAsync(m => m.TemplateKey == templateKey, cancellationToken);

    public void Add(WhatsAppTemplateMapping aggregate) => _dbContext.WhatsAppTemplateMappings.Add(aggregate);

    public void Update(WhatsAppTemplateMapping aggregate) => _dbContext.WhatsAppTemplateMappings.Update(aggregate);

    public void Remove(WhatsAppTemplateMapping aggregate) => _dbContext.WhatsAppTemplateMappings.Remove(aggregate);
}
