using IHostPro.BuildingBlocks.Application;
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

    public Task<PagedResult<AirbnbEmailMessageReceiptResult>> ListForCurrentTenantAsync(
        string? status, string? reasonCode, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Added.AsEnumerable();

        if (status is not null)
        {
            if (!Enum.TryParse<AirbnbEmailMessageProcessingStatus>(status, ignoreCase: true, out var statusEnum))
                return Task.FromResult(new PagedResult<AirbnbEmailMessageReceiptResult>(page, pageSize, 0, []));

            query = query.Where(r => r.ProcessingStatus == statusEnum);
        }

        if (reasonCode is not null)
            query = query.Where(r => r.FailureReason == reasonCode);

        var ordered = query.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id).ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(ToResult).ToArray();

        return Task.FromResult(new PagedResult<AirbnbEmailMessageReceiptResult>(page, pageSize, ordered.Count, items));
    }

    private static AirbnbEmailMessageReceiptResult ToResult(AirbnbEmailMessageReceipt receipt) => new(
        receipt.Id, receipt.ReceivedAtUtc, receipt.ProcessingStatus.ToString(), receipt.DetectedEventType,
        receipt.ParserVersion, receipt.FailureReason, receipt.UnmatchedListingTitle, receipt.ProcessedAtUtc, receipt.CreatedAtUtc);

    public void Add(AirbnbEmailMessageReceipt aggregate) => Added.Add(aggregate);

    public void Update(AirbnbEmailMessageReceipt aggregate)
    {
    }

    public void Remove(AirbnbEmailMessageReceipt aggregate) => Added.Remove(aggregate);
}
