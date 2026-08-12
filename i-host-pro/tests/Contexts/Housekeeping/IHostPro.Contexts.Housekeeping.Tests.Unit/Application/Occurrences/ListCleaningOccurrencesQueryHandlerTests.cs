using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Application.Occurrences;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Occurrences;

public class ListCleaningOccurrencesQueryHandlerTests
{
    private static readonly Guid HousekeeperUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CleaningResult OwnedDetail(Guid cleaningId) => new(
        cleaningId, Guid.NewGuid(), null, HousekeeperUserId, "Started", Guid.NewGuid(), Now, null, Now, null, null, null);

    [Fact]
    public async Task Returns_the_occurrences_the_reader_produces_for_an_owned_cleaning()
    {
        var cleaningId = Guid.NewGuid();
        var occurrence = new CleaningOccurrenceResult(Guid.NewGuid(), cleaningId, "Noise", null, HousekeeperUserId, Now);
        var cleaningReader = FakeCleaningReader.WithDetail(OwnedDetail(cleaningId));
        var occurrenceReader = FakeCleaningOccurrenceReader.WithResults([occurrence]);
        var handler = new ListCleaningOccurrencesQueryHandler(cleaningReader, occurrenceReader);

        var result = await handler.Handle(new ListCleaningOccurrencesQuery(cleaningId, HousekeeperUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Should().Be(occurrence);
        occurrenceReader.LastCleaningId.Should().Be(cleaningId);
        occurrenceReader.LastHousekeeperUserId.Should().Be(HousekeeperUserId);
    }

    [Fact]
    public async Task A_cleaning_not_owned_by_the_caller_fails_with_CleaningNotFound_and_never_queries_occurrences()
    {
        var cleaningReader = FakeCleaningReader.WithDetail(null);
        var occurrenceReader = FakeCleaningOccurrenceReader.WithResults([]);
        var handler = new ListCleaningOccurrencesQueryHandler(cleaningReader, occurrenceReader);

        var result = await handler.Handle(new ListCleaningOccurrencesQuery(Guid.NewGuid(), HousekeeperUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
        occurrenceReader.LastCleaningId.Should().BeNull();
    }
}
