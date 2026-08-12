using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Checklist;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Checklist;

public class SetOwnCleaningChecklistItemCommandHandlerTests
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
    public async Task Setting_a_never_toggled_item_creates_a_new_row()
    {
        var cleaning = StartedCleaningFor(HousekeeperUserId);
        var cleaningRepository = FakeCleaningRepository.WithCleaning(cleaning);
        var checklistRepository = FakeCleaningChecklistItemRepository.WithExistingItem(null);
        var handler = new SetOwnCleaningChecklistItemCommandHandler(
            new PassThroughHousekeepingTransactionExecutor(), cleaningRepository, checklistRepository,
            new FakeHousekeepingAuditWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetOwnCleaningChecklistItemCommand(TenantId, HousekeeperUserId, cleaning.Id, "Stove", true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsChecked.Should().BeTrue();
        result.Value.ItemType.Should().Be("Stove");
        checklistRepository.AddedItems.Should().ContainSingle(i => i.ItemType == ChecklistItemType.Stove && i.IsChecked);
    }

    [Fact]
    public async Task Setting_an_already_persisted_item_mutates_it_in_place_without_adding_a_new_row()
    {
        var cleaning = StartedCleaningFor(HousekeeperUserId);
        var existingItem = CleaningChecklistItem.Create(
            Guid.NewGuid(), TenantId, cleaning.Id, ChecklistItemType.Window, false, HousekeeperUserId, Now.AddMinutes(-5));
        var cleaningRepository = FakeCleaningRepository.WithCleaning(cleaning);
        var checklistRepository = FakeCleaningChecklistItemRepository.WithExistingItem(existingItem);
        var handler = new SetOwnCleaningChecklistItemCommandHandler(
            new PassThroughHousekeepingTransactionExecutor(), cleaningRepository, checklistRepository,
            new FakeHousekeepingAuditWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetOwnCleaningChecklistItemCommand(TenantId, HousekeeperUserId, cleaning.Id, "Window", true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsChecked.Should().BeTrue();
        existingItem.IsChecked.Should().BeTrue();
        checklistRepository.AddedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Setting_by_a_different_housekeeper_fails_with_CleaningNotFound_never_Forbidden()
    {
        var cleaning = StartedCleaningFor(HousekeeperUserId);
        var cleaningRepository = FakeCleaningRepository.WithCleaning(cleaning);
        var checklistRepository = FakeCleaningChecklistItemRepository.WithExistingItem(null);
        var handler = new SetOwnCleaningChecklistItemCommandHandler(
            new PassThroughHousekeepingTransactionExecutor(), cleaningRepository, checklistRepository,
            new FakeHousekeepingAuditWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetOwnCleaningChecklistItemCommand(TenantId, OtherHousekeeperUserId, cleaning.Id, "Stove", true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
        checklistRepository.AddedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Setting_on_a_Completed_cleaning_fails_with_InvalidCleaningTransition()
    {
        var cleaning = CompletedCleaningFor(HousekeeperUserId);
        var cleaningRepository = FakeCleaningRepository.WithCleaning(cleaning);
        var checklistRepository = FakeCleaningChecklistItemRepository.WithExistingItem(null);
        var handler = new SetOwnCleaningChecklistItemCommandHandler(
            new PassThroughHousekeepingTransactionExecutor(), cleaningRepository, checklistRepository,
            new FakeHousekeepingAuditWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetOwnCleaningChecklistItemCommand(TenantId, HousekeeperUserId, cleaning.Id, "Trash", true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
        checklistRepository.AddedItems.Should().BeEmpty();
    }
}
