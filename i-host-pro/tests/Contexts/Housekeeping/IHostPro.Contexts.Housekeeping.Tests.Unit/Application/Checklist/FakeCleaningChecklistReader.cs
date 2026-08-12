using IHostPro.Contexts.Housekeeping.Application.Checklist;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Checklist;

internal sealed class FakeCleaningChecklistReader : ICleaningChecklistReader
{
    private readonly IReadOnlyList<CleaningChecklistItemResult> _results;

    private FakeCleaningChecklistReader(IReadOnlyList<CleaningChecklistItemResult> results) => _results = results;

    public static FakeCleaningChecklistReader WithResults(IReadOnlyList<CleaningChecklistItemResult> results) => new(results);

    public Guid? LastCleaningId { get; private set; }

    public Task<IReadOnlyList<CleaningChecklistItemResult>> GetForCleaningAsync(Guid cleaningId, CancellationToken cancellationToken)
    {
        LastCleaningId = cleaningId;
        return Task.FromResult(_results);
    }
}
