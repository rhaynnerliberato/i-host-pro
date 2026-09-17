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

    public Task<IReadOnlyDictionary<AirbnbEmailMessageProcessingStatus, int>> CountByProcessingStatusForCurrentTenantAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<AirbnbEmailMessageProcessingStatus, int> counts = Added
            .GroupBy(r => r.ProcessingStatus)
            .ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(counts);
    }

    public void Add(AirbnbEmailMessageReceipt aggregate) => Added.Add(aggregate);

    public void Update(AirbnbEmailMessageReceipt aggregate)
    {
    }

    public void Remove(AirbnbEmailMessageReceipt aggregate) => Added.Remove(aggregate);
}
