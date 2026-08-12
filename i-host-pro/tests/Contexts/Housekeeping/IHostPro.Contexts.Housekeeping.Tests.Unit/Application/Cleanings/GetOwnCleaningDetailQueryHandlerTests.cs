using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Errors;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

public class GetOwnCleaningDetailQueryHandlerTests
{
    [Fact]
    public async Task A_cleaning_assigned_to_the_caller_returns_its_detail()
    {
        var cleaningId = Guid.NewGuid();
        var housekeeperUserId = Guid.NewGuid();
        var detail = new CleaningResult(
            cleaningId, Guid.NewGuid(), null, housekeeperUserId, "Assigned", Guid.NewGuid(),
            DateTimeOffset.UtcNow, null, null, null, null, null);
        var handler = new GetOwnCleaningDetailQueryHandler(FakeCleaningReader.WithDetail(detail));

        var result = await handler.Handle(new(cleaningId, housekeeperUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(cleaningId);
    }

    [Fact]
    public async Task A_cleaning_assigned_to_someone_else_returns_not_found_never_forbidden()
    {
        var cleaningId = Guid.NewGuid();
        var detail = new CleaningResult(
            cleaningId, Guid.NewGuid(), null, Guid.NewGuid(), "Assigned", Guid.NewGuid(),
            DateTimeOffset.UtcNow, null, null, null, null, null);
        var handler = new GetOwnCleaningDetailQueryHandler(FakeCleaningReader.WithDetail(detail));

        var result = await handler.Handle(new(cleaningId, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    [Fact]
    public async Task A_nonexistent_cleaning_returns_the_same_error_as_one_belonging_to_someone_else()
    {
        var handler = new GetOwnCleaningDetailQueryHandler(FakeCleaningReader.WithDetail(null));

        var result = await handler.Handle(new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }
}
