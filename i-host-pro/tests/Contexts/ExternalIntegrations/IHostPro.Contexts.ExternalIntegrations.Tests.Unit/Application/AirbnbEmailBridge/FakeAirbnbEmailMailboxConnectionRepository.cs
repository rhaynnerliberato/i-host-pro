using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

internal sealed class FakeAirbnbEmailMailboxConnectionRepository : IAirbnbEmailMailboxConnectionRepository
{
    private AirbnbEmailMailboxConnection? _current;

    public static FakeAirbnbEmailMailboxConnectionRepository WithExisting(AirbnbEmailMailboxConnection? existing)
    {
        var repository = new FakeAirbnbEmailMailboxConnectionRepository();
        repository._current = existing;
        return repository;
    }

    public AirbnbEmailMailboxConnection? Current => _current;

    public Task<AirbnbEmailMailboxConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_current?.Id == id ? _current : null);

    public Task<AirbnbEmailMailboxConnection?> GetForCurrentTenantAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_current);

    public void Add(AirbnbEmailMailboxConnection aggregate) => _current = aggregate;

    public void Update(AirbnbEmailMailboxConnection aggregate) => _current = aggregate;

    public void Remove(AirbnbEmailMailboxConnection aggregate) => _current = null;
}
