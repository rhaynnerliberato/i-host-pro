using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakeCleaningReader : ICleaningReader
{
    private readonly CleaningResult? _detail;
    private readonly IReadOnlyList<CleaningSummaryResult> _summaries;

    private FakeCleaningReader(CleaningResult? detail, IReadOnlyList<CleaningSummaryResult> summaries)
    {
        _detail = detail;
        _summaries = summaries;
    }

    public static FakeCleaningReader WithDetail(CleaningResult? detail) => new(detail, []);

    public static FakeCleaningReader WithSummaries(IReadOnlyList<CleaningSummaryResult> summaries) => new(null, summaries);

    public string? LastStatus { get; private set; }
    public Guid? LastPropertyId { get; private set; }
    public Guid? LastAssignedHousekeeperUserId { get; private set; }
    public int? LastPage { get; private set; }
    public int? LastPageSize { get; private set; }

    public Task<PagedResult<CleaningSummaryResult>> ListAsync(
        string? status, Guid? propertyId, Guid? assignedHousekeeperUserId,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        LastStatus = status;
        LastPropertyId = propertyId;
        LastAssignedHousekeeperUserId = assignedHousekeeperUserId;
        LastPage = page;
        LastPageSize = pageSize;

        return Task.FromResult(new PagedResult<CleaningSummaryResult>(page, pageSize, _summaries.Count, _summaries));
    }

    public Task<CleaningResult?> GetByIdAsync(Guid cleaningId, CancellationToken cancellationToken) =>
        Task.FromResult(_detail);
}
