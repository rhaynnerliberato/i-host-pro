using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Domain;

public class LateCheckoutRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset RequestedCheckOutAt = Now.AddDays(4);

    private static LateCheckoutRequest CreateValid(
        LateCheckoutChargeType chargeType = LateCheckoutChargeType.None, decimal? chargeValue = null, bool requiresPix = false) =>
        LateCheckoutRequest.Create(
            Guid.NewGuid(), TenantId, ReservationId, PropertyId, RequestedCheckOutAt, chargeType, chargeValue, requiresPix, Now);

    [Fact]
    public void Create_with_valid_data_starts_as_Pending_with_no_decision()
    {
        var request = CreateValid(LateCheckoutChargeType.FixedAmount, 50m, requiresPix: true);

        request.TenantId.Should().Be(TenantId);
        request.ReservationId.Should().Be(ReservationId);
        request.PropertyId.Should().Be(PropertyId);
        request.RequestedCheckOutAt.Should().Be(RequestedCheckOutAt);
        request.ChargeType.Should().Be(LateCheckoutChargeType.FixedAmount);
        request.ChargeValue.Should().Be(50m);
        request.RequiresPix.Should().BeTrue();
        request.Status.Should().Be(LateCheckoutRequestStatus.Pending);
        request.DenialReason.Should().BeNull();
        request.DecidedAtUtc.Should().BeNull();
        request.CreatedAtUtc.Should().Be(Now);
        request.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_rejects_empty_ReservationId()
    {
        var act = () => LateCheckoutRequest.Create(
            Guid.NewGuid(), TenantId, Guid.Empty, PropertyId, RequestedCheckOutAt, LateCheckoutChargeType.None, null, false, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_empty_PropertyId()
    {
        var act = () => LateCheckoutRequest.Create(
            Guid.NewGuid(), TenantId, ReservationId, Guid.Empty, RequestedCheckOutAt, LateCheckoutChargeType.None, null, false, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_Percentage_charge_type()
    {
        var act = () => LateCheckoutRequest.Create(
            Guid.NewGuid(), TenantId, ReservationId, PropertyId, RequestedCheckOutAt, LateCheckoutChargeType.Percentage, 10m, false, Now);

        act.Should().Throw<ArgumentException>(
            "Percentage is officially unsupported (Fase 10, Checkpoint 3 mandate) — the handler must reject it before ever calling Create");
    }

    [Fact]
    public void Approve_from_Pending_transitions_to_Approved_and_stamps_the_decision()
    {
        var request = CreateValid();
        var decidedAt = Now.AddMinutes(1);

        request.Approve(decidedAt);

        request.Status.Should().Be(LateCheckoutRequestStatus.Approved);
        request.DecidedAtUtc.Should().Be(decidedAt);
        request.UpdatedAtUtc.Should().Be(decidedAt);
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
    public void MarkPendingPayment_from_Pending_transitions_to_PendingPayment_without_stamping_a_decision()
    {
        var request = CreateValid();
        var now = Now.AddMinutes(1);

        request.MarkPendingPayment(now);

        request.Status.Should().Be(LateCheckoutRequestStatus.PendingPayment);
        request.UpdatedAtUtc.Should().Be(now);
        request.DecidedAtUtc.Should().BeNull("PendingPayment is not a final decision — DecidedAtUtc is reserved for Approve/Deny");
    }

    [Fact]
    public void MarkPendingPayment_when_not_Pending_throws()
    {
        var request = CreateValid();
        request.MarkPendingPayment(Now.AddMinutes(1));

        var act = () => request.MarkPendingPayment(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Deny_from_Pending_transitions_to_Denied_and_stamps_the_reason_and_decision()
    {
        var request = CreateValid();
        var decidedAt = Now.AddMinutes(1);

        request.Deny(LateCheckoutDenialReason.AfterLatestTime, decidedAt);

        request.Status.Should().Be(LateCheckoutRequestStatus.Denied);
        request.DenialReason.Should().Be(LateCheckoutDenialReason.AfterLatestTime);
        request.DecidedAtUtc.Should().Be(decidedAt);
        request.UpdatedAtUtc.Should().Be(decidedAt);
    }

    [Fact]
    public void Deny_when_not_Pending_throws()
    {
        var request = CreateValid();
        request.Deny(LateCheckoutDenialReason.PolicyNotAllowed, Now.AddMinutes(1));

        var act = () => request.Deny(LateCheckoutDenialReason.AfterLatestTime, Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }
}
