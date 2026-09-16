using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

internal sealed class FakeAirbnbEmailSyncStateRepository : IAirbnbEmailSyncStateRepository
{
    private AirbnbEmailSyncState? _current;

    public AirbnbEmailSyncState? Current => _current;

    public Task<AirbnbEmailSyncState?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_current?.Id == id ? _current : null);

    public Task<AirbnbEmailSyncState?> GetForCurrentTenantAsync(Guid mailboxConnectionId, string mailFolderId, CancellationToken cancellationToken) =>
        Task.FromResult(_current is not null && _current.MailboxConnectionId == mailboxConnectionId && _current.MailFolderId == mailFolderId
            ? _current
            : null);

    public void Add(AirbnbEmailSyncState aggregate) => _current = aggregate;

    public void Update(AirbnbEmailSyncState aggregate) => _current = aggregate;

    public void Remove(AirbnbEmailSyncState aggregate) => _current = null;
}
