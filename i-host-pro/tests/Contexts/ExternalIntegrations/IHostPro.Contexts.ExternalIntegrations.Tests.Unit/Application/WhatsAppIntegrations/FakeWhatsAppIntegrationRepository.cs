using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppIntegrations;

internal sealed class FakeWhatsAppIntegrationRepository : IWhatsAppIntegrationRepository
{
    private WhatsAppIntegration? _current;

    public static FakeWhatsAppIntegrationRepository WithExisting(WhatsAppIntegration? existing)
    {
        var repository = new FakeWhatsAppIntegrationRepository();
        repository._current = existing;
        return repository;
    }

    public List<WhatsAppIntegration> AddedIntegrations { get; } = [];

    public Task<WhatsAppIntegration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_current?.Id == id ? _current : null);

    public Task<WhatsAppIntegration?> GetForCurrentTenantAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_current);

    public void Add(WhatsAppIntegration aggregate)
    {
        _current = aggregate;
        AddedIntegrations.Add(aggregate);
    }

    public void Update(WhatsAppIntegration aggregate) => _current = aggregate;

    public void Remove(WhatsAppIntegration aggregate) => _current = null;
}
