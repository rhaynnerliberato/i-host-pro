using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Domain;

public class EarlyCheckInRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset RequestedCheckInAt = Now.AddDays(4);

    private static EarlyCheckInRequest CreateValid() =>
        EarlyCheckInRequest.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, RequestedCheckInAt, Now);

    [Fact]
    public void Create_with_valid_data_starts_as_Pending_with_no_decision()
    {
        var request = CreateValid();

        request.TenantId.Should().Be(TenantId);
        request.ReservationId.Should().Be(ReservationId);
        request.PropertyId.Should().Be(PropertyId);
        request.RequestedCheckInAt.Should().Be(RequestedCheckInAt);
        request.Status.Should().Be(EarlyCheckInRequestStatus.Pending);
        request.DenialReason.Should().BeNull();
        request.DecidedAtUtc.Should().BeNull();
        request.CreatedAtUtc.Should().Be(Now);
        request.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_rejects_empty_ReservationId()
    {
        var act = () => EarlyCheckInRequest.Create(Guid.NewGuid(), TenantId, Guid.Empty, PropertyId, RequestedCheckInAt, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_empty_PropertyId()
    {
        var act = () => EarlyCheckInRequest.Create(Guid.NewGuid(), TenantId, ReservationId, Guid.Empty, RequestedCheckInAt, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Approve_from_Pending_transitions_to_Approved_and_stamps_the_decision()
    {
        var request = CreateValid();
        var decidedAt = Now.AddMinutes(1);

        request.Approve(decidedAt);

        request.Status.Should().Be(EarlyCheckInRequestStatus.Approved);
        request.DecidedAtUtc.Should().Be(decidedAt);
        request.UpdatedAtUtc.Should().Be(decidedAt);
        request.DenialReason.Should().BeNull();
    }

    [Fact]
    public void Approve_when_not_Pending_throws()
    {
        var request = CreateValid();
        request.Approve(Now.AddMinutes(1));

        var act = () => request.Approve(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Deny_from_Pending_transitions_to_Denied_and_stamps_the_reason_and_decision()
    {
        var request = CreateValid();
        var decidedAt = Now.AddMinutes(1);

        request.Deny(EarlyCheckInDenialReason.ScheduleConflict, decidedAt);

        request.Status.Should().Be(EarlyCheckInRequestStatus.Denied);
        request.DenialReason.Should().Be(EarlyCheckInDenialReason.ScheduleConflict);
        request.DecidedAtUtc.Should().Be(decidedAt);
        request.UpdatedAtUtc.Should().Be(decidedAt);
    }

    [Fact]
    public void Deny_when_not_Pending_throws()
    {
        var request = CreateValid();
        request.Deny(EarlyCheckInDenialReason.PolicyNotAllowed, Now.AddMinutes(1));

        var act = () => request.Deny(EarlyCheckInDenialReason.ScheduleConflict, Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }
}
