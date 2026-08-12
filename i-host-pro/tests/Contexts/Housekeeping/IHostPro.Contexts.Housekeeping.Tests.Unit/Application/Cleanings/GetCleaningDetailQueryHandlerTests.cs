using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

public class GetCleaningDetailQueryHandlerTests
{
    [Fact]
    public async Task An_existing_cleaning_returns_its_detail()
    {
        var cleaningId = Guid.NewGuid();
        var detail = new CleaningResult(
            cleaningId, Guid.NewGuid(), null, null, "Pending", Guid.NewGuid(),
            DateTimeOffset.UtcNow, null, null, null, null, null);
        var handler = new GetCleaningDetailQueryHandler(FakeCleaningReader.WithDetail(detail));

        var result = await handler.Handle(new(cleaningId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(cleaningId);
    }

    [Fact]
    public async Task A_nonexistent_or_cross_tenant_cleaning_fails_with_CleaningNotFound()
    {
        var handler = new GetCleaningDetailQueryHandler(FakeCleaningReader.WithDetail(null));

        var result = await handler.Handle(new(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }
}
