using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

internal sealed class FakeAirbnbEmailMessageReceiptRepository : IAirbnbEmailMessageReceiptRepository
{
    public List<AirbnbEmailMessageReceipt> Added { get; } = [];

    public Task<AirbnbEmailMessageReceipt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.FirstOrDefault(r => r.Id == id));

    public Task<bool> ExistsForCurrentTenantAsync(string graphMessageId, CancellationToken cancellationToken) =>
        Task.FromResult(Added.Any(r => r.GraphMessageId == graphMessageId));

    public void Add(AirbnbEmailMessageReceipt aggregate) => Added.Add(aggregate);

    public void Update(AirbnbEmailMessageReceipt aggregate)
    {
    }

    public void Remove(AirbnbEmailMessageReceipt aggregate) => Added.Remove(aggregate);
}
