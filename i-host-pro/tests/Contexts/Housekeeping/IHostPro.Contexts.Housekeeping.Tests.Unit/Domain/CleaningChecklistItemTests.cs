using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Domain;

public class CleaningChecklistItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_stores_every_field_verbatim()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var updatedByUserId = Guid.NewGuid();

        var item = CleaningChecklistItem.Create(id, tenantId, cleaningId, ChecklistItemType.Stove, true, updatedByUserId, Now);

        item.Id.Should().Be(id);
        item.TenantId.Should().Be(tenantId);
        item.CleaningId.Should().Be(cleaningId);
        item.ItemType.Should().Be(ChecklistItemType.Stove);
        item.IsChecked.Should().BeTrue();
        item.UpdatedByUserId.Should().Be(updatedByUserId);
        item.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void SetChecked_toggles_state_and_stamps_the_updater()
    {
        var item = CleaningChecklistItem.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ChecklistItemType.Window, false, Guid.NewGuid(), Now);
        var secondUpdater = Guid.NewGuid();

        item.SetChecked(true, secondUpdater, Now.AddMinutes(5));

        item.IsChecked.Should().BeTrue();
        item.UpdatedByUserId.Should().Be(secondUpdater);
        item.UpdatedAtUtc.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public void Create_normalizes_the_update_instant_to_UTC()
    {
        var localNow = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(-3));

        var item = CleaningChecklistItem.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ChecklistItemType.Bathroom, false, Guid.NewGuid(), localNow);

        item.UpdatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        item.UpdatedAtUtc.Should().Be(localNow.ToUniversalTime());
    }
}
