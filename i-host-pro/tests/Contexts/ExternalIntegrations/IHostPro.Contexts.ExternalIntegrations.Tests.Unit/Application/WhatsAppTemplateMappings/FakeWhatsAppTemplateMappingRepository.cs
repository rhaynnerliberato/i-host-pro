using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppTemplateMappings;

internal sealed class FakeWhatsAppTemplateMappingRepository : IWhatsAppTemplateMappingRepository
{
    private readonly Dictionary<string, WhatsAppTemplateMapping> _byTemplateKey = new();

    public static FakeWhatsAppTemplateMappingRepository WithExisting(WhatsAppTemplateMapping? existing)
    {
        var repository = new FakeWhatsAppTemplateMappingRepository();
        if (existing is not null)
            repository._byTemplateKey[existing.TemplateKey] = existing;
        return repository;
    }

    public List<WhatsAppTemplateMapping> AddedMappings { get; } = [];

    public Task<WhatsAppTemplateMapping?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byTemplateKey.Values.FirstOrDefault(m => m.Id == id));

    public Task<WhatsAppTemplateMapping?> GetForCurrentTenantByTemplateKeyAsync(string templateKey, CancellationToken cancellationToken) =>
        Task.FromResult(_byTemplateKey.GetValueOrDefault(templateKey));

    public void Add(WhatsAppTemplateMapping aggregate)
    {
        _byTemplateKey[aggregate.TemplateKey] = aggregate;
        AddedMappings.Add(aggregate);
    }

    public void Update(WhatsAppTemplateMapping aggregate) => _byTemplateKey[aggregate.TemplateKey] = aggregate;

    public void Remove(WhatsAppTemplateMapping aggregate) => _byTemplateKey.Remove(aggregate.TemplateKey);
}
