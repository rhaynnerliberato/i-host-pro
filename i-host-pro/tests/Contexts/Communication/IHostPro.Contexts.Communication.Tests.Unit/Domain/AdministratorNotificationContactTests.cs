using FluentAssertions;
using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Tests.Unit.Domain;

/// <summary>Fase 11, Checkpoint 6 (Human Handoff, Safety &amp; Audit) — create/change/deactivate/reactivate lifecycle, invariants.</summary>
public class AdministratorNotificationContactTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string Phone = "+5511999999999";

    [Fact]
    public void Create_with_valid_data_starts_Active()
    {
        var contact = AdministratorNotificationContact.Create(Guid.NewGuid(), TenantId, Phone, Now);

        contact.TenantId.Should().Be(TenantId);
        contact.DestinationPhone.Should().Be(Phone);
        contact.IsActive.Should().BeTrue();
        contact.CreatedAtUtc.Should().Be(Now);
        contact.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_rejects_empty_destination_phone()
    {
        var act = () => AdministratorNotificationContact.Create(Guid.NewGuid(), TenantId, "", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ChangeDestinationPhone_replaces_the_value_and_updates_the_timestamp()
    {
        var contact = AdministratorNotificationContact.Create(Guid.NewGuid(), TenantId, Phone, Now);
        var changedAt = Now.AddMinutes(1);

        contact.ChangeDestinationPhone("+5511888888888", changedAt);

        contact.DestinationPhone.Should().Be("+5511888888888");
        contact.UpdatedAtUtc.Should().Be(changedAt);
    }

    [Fact]
    public void ChangeDestinationPhone_rejects_empty_value()
    {
        var contact = AdministratorNotificationContact.Create(Guid.NewGuid(), TenantId, Phone, Now);

        var act = () => contact.ChangeDestinationPhone("", Now.AddMinutes(1));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Deactivate_then_Reactivate_round_trips_IsActive()
    {
        var contact = AdministratorNotificationContact.Create(Guid.NewGuid(), TenantId, Phone, Now);

        contact.Deactivate(Now.AddMinutes(1));
        contact.IsActive.Should().BeFalse();

        contact.Reactivate(Now.AddMinutes(2));
        contact.IsActive.Should().BeTrue();
    }
}
