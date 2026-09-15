using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

internal sealed class FakeAirbnbEmailTokenCacheStore : IAirbnbEmailTokenCacheStore
{
    private readonly FakeAirbnbEmailMailboxConnectionRepository _repository;

    public FakeAirbnbEmailTokenCacheStore(FakeAirbnbEmailMailboxConnectionRepository repository) => _repository = repository;

    public bool ClearWasCalled { get; private set; }

    public Task<byte[]?> LoadAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_repository.Current?.TokenCacheBlob);

    public Task SaveAsync(Guid tenantId, byte[] tokenCacheBytes, CancellationToken cancellationToken)
    {
        _repository.Current?.UpdateTokenCache(tokenCacheBytes, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task ClearAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        ClearWasCalled = true;
        _repository.Current?.Disconnect(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
