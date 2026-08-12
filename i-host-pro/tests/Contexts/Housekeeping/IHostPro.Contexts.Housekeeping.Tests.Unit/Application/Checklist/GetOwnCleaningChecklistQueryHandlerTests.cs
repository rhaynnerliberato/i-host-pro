using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Checklist;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Checklist;

public class GetOwnCleaningChecklistQueryHandlerTests
{
    private static readonly Guid HousekeeperUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CleaningResult OwnedDetail(Guid cleaningId) => new(
        cleaningId, Guid.NewGuid(), null, HousekeeperUserId, "Started", Guid.NewGuid(), Now, null, Now, null, null, null);

    [Fact]
    public async Task Returns_the_items_the_reader_produces_for_an_owned_cleaning()
    {
        var cleaningId = Guid.NewGuid();
        var items = new[] { new CleaningChecklistItemResult(cleaningId, "Stove", true, HousekeeperUserId, Now) };
        var cleaningReader = FakeCleaningReader.WithDetail(OwnedDetail(cleaningId));
        var checklistReader = FakeCleaningChecklistReader.WithResults(items);
        var handler = new GetOwnCleaningChecklistQueryHandler(cleaningReader, checklistReader);

        var result = await handler.Handle(new GetOwnCleaningChecklistQuery(cleaningId, HousekeeperUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(items);
        checklistReader.LastCleaningId.Should().Be(cleaningId);
    }

    [Fact]
    public async Task A_cleaning_not_owned_by_the_caller_fails_with_CleaningNotFound_and_never_queries_the_checklist()
    {
        var cleaningReader = FakeCleaningReader.WithDetail(null);
        var checklistReader = FakeCleaningChecklistReader.WithResults([]);
        var handler = new GetOwnCleaningChecklistQueryHandler(cleaningReader, checklistReader);

        var result = await handler.Handle(new GetOwnCleaningChecklistQuery(Guid.NewGuid(), HousekeeperUserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
        checklistReader.LastCleaningId.Should().BeNull();
    }
}
