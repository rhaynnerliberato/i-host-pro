using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Application.Occurrences;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Occurrences;

public class RegisterCleaningOccurrenceCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid HousekeeperUserId = Guid.NewGuid();
    private static readonly Guid OtherHousekeeperUserId = Guid.NewGuid();

    private static Cleaning StartedCleaningFor(Guid housekeeperUserId)
    {
        var cleaning = Cleaning.Create(Guid.NewGuid(), TenantId, Guid.NewGuid(), null, Guid.NewGuid(), Now.AddMinutes(-10));
        cleaning.Assign(housekeeperUserId, Now.AddMinutes(-9));
        cleaning.Start(Now.AddMinutes(-8));
        return cleaning;
    }

    private static Cleaning CompletedCleaningFor(Guid housekeeperUserId)
    {
        var cleaning = StartedCleaningFor(housekeeperUserId);
        cleaning.StartInspection(Now.AddMinutes(-7));
        cleaning.Complete(Now.AddMinutes(-6));
        return cleaning;
    }

    [Fact]
    public async Task Registering_an_occurrence_on_the_callers_own_cleaning_succeeds_and_records_it()
    {
        var cleaning = StartedCleaningFor(HousekeeperUserId);
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var writer = new FakeCleaningOccurrenceWriter();
        var handler = new RegisterCleaningOccurrenceCommandHandler(
            new PassThroughHousekeepingTransactionExecutor(), repository, writer, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new RegisterCleaningOccurrenceCommand(TenantId, HousekeeperUserId, cleaning.Id, "Damage", "Broken lamp"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Type.Should().Be("Damage");
        result.Value.Description.Should().Be("Broken lamp");
        result.Value.RegisteredByUserId.Should().Be(HousekeeperUserId);
        writer.RecordedOccurrences.Should().ContainSingle(o => o.CleaningId == cleaning.Id && o.Type == OccurrenceType.Damage);
    }

    [Fact]
    public async Task Registering_by_a_different_housekeeper_fails_with_CleaningNotFound_never_Forbidden()
    {
        var cleaning = StartedCleaningFor(HousekeeperUserId);
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var writer = new FakeCleaningOccurrenceWriter();
        var handler = new RegisterCleaningOccurrenceCommandHandler(
            new PassThroughHousekeepingTransactionExecutor(), repository, writer, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new RegisterCleaningOccurrenceCommand(TenantId, OtherHousekeeperUserId, cleaning.Id, "Noise", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
        writer.RecordedOccurrences.Should().BeEmpty();
    }

    [Fact]
    public async Task Registering_on_a_Completed_cleaning_fails_with_InvalidCleaningTransition()
    {
        var cleaning = CompletedCleaningFor(HousekeeperUserId);
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var writer = new FakeCleaningOccurrenceWriter();
        var handler = new RegisterCleaningOccurrenceCommandHandler(
            new PassThroughHousekeepingTransactionExecutor(), repository, writer, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new RegisterCleaningOccurrenceCommand(TenantId, HousekeeperUserId, cleaning.Id, "Theft", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
        writer.RecordedOccurrences.Should().BeEmpty();
    }

    [Fact]
    public async Task Registering_for_a_nonexistent_cleaning_fails_with_CleaningNotFound()
    {
        var repository = FakeCleaningRepository.WithCleaning(null);
        var handler = new RegisterCleaningOccurrenceCommandHandler(
            new PassThroughHousekeepingTransactionExecutor(), repository, new FakeCleaningOccurrenceWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new RegisterCleaningOccurrenceCommand(TenantId, HousekeeperUserId, Guid.NewGuid(), "Breakage", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }
}
