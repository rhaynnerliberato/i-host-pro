using IHostPro.Contexts.Housekeeping.Application.Occurrences;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Occurrences;

internal sealed class FakeCleaningOccurrenceReader : ICleaningOccurrenceReader
{
    private readonly IReadOnlyList<CleaningOccurrenceResult> _results;

    private FakeCleaningOccurrenceReader(IReadOnlyList<CleaningOccurrenceResult> results) => _results = results;

    public static FakeCleaningOccurrenceReader WithResults(IReadOnlyList<CleaningOccurrenceResult> results) => new(results);

    public Guid? LastCleaningId { get; private set; }
    public Guid? LastHousekeeperUserId { get; private set; }

    public Task<IReadOnlyList<CleaningOccurrenceResult>> ListForOwnCleaningAsync(
        Guid cleaningId, Guid housekeeperUserId, CancellationToken cancellationToken)
    {
        LastCleaningId = cleaningId;
        LastHousekeeperUserId = housekeeperUserId;
        return Task.FromResult(_results);
    }
}
