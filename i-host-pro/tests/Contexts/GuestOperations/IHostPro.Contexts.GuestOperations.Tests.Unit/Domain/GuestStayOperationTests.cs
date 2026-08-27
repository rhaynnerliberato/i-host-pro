using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Domain;

public class GuestStayOperationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();

    private static GuestStayOperation CreateValid() =>
        GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, Now);

    [Fact]
    public void Create_with_valid_data_starts_as_Active_with_no_checkout_timestamp()
    {
        var operation = CreateValid();

        operation.TenantId.Should().Be(TenantId);
        operation.ReservationId.Should().Be(ReservationId);
        operation.PropertyId.Should().Be(PropertyId);
        operation.Status.Should().Be(GuestStayOperationStatus.Active);
        operation.CheckedInAtUtc.Should().BeNull();
        operation.CheckedOutAtUtc.Should().BeNull();
        operation.CreatedAtUtc.Should().Be(Now);
        operation.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_rejects_empty_ReservationId()
    {
        var act = () => GuestStayOperation.Create(Guid.NewGuid(), TenantId, Guid.Empty, PropertyId, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_empty_PropertyId()
    {
        var act = () => GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, Guid.Empty, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CheckOut_from_Active_transitions_to_CheckedOut_and_stamps_the_timestamp()
    {
        var operation = CreateValid();
        var checkedOutAt = Now.AddDays(3);

        operation.CheckOut(checkedOutAt);

        operation.Status.Should().Be(GuestStayOperationStatus.CheckedOut);
        operation.CheckedOutAtUtc.Should().Be(checkedOutAt);
        operation.UpdatedAtUtc.Should().Be(checkedOutAt);
    }

    [Fact]
    public void CheckOut_when_already_CheckedOut_throws()
    {
        var operation = CreateValid();
        operation.CheckOut(Now.AddDays(3));

        var act = () => operation.CheckOut(Now.AddDays(4));

        act.Should().Throw<InvalidOperationException>(
            "this guard is defense-in-depth — the handler is responsible for the real idempotent no-op BEFORE calling CheckOut");
    }
}
