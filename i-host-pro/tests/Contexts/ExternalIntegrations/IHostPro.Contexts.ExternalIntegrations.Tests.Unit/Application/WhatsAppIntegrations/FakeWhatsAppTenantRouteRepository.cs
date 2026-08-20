using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppIntegrations;

internal sealed class FakeWhatsAppTenantRouteRepository : IWhatsAppTenantRouteRepository
{
    private WhatsAppTenantRoute? _current;

    public static FakeWhatsAppTenantRouteRepository WithExisting(WhatsAppTenantRoute? existing)
    {
        var repository = new FakeWhatsAppTenantRouteRepository();
        repository._current = existing;
        return repository;
    }

    public List<WhatsAppTenantRoute> AddedRoutes { get; } = [];
    public List<WhatsAppTenantRoute> RemovedRoutes { get; } = [];
    public WhatsAppTenantRoute? Current => _current;

    public Task<WhatsAppTenantRoute?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_current?.TenantId == tenantId ? _current : null);

    public Task<WhatsAppTenantRoute?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken) =>
        Task.FromResult(_current?.PhoneNumberId == phoneNumberId ? _current : null);

    public void Add(WhatsAppTenantRoute route)
    {
        _current = route;
        AddedRoutes.Add(route);
    }

    public void Remove(WhatsAppTenantRoute route)
    {
        RemovedRoutes.Add(route);
        _current = null;
    }
}
