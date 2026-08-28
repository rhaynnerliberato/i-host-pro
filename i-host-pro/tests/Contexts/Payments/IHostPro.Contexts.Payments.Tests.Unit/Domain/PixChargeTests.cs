using FluentAssertions;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Domain.Enums;

namespace IHostPro.Contexts.Payments.Tests.Unit.Domain;

public class PixChargeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid LateCheckoutRequestId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private static PixCharge CreateValid(decimal amount = 100m, string currencyCode = "BRL") =>
        PixCharge.Create(Guid.NewGuid(), TenantId, LateCheckoutRequestId, ReservationId, amount, currencyCode, Now);

    // ---- Create ----

    [Fact]
    public void Create_with_valid_data_starts_as_Pending_with_a_generated_idempotency_key()
    {
        var charge = CreateValid(100m);

        charge.TenantId.Should().Be(TenantId);
        charge.LateCheckoutRequestId.Should().Be(LateCheckoutRequestId);
        charge.ReservationId.Should().Be(ReservationId);
        charge.Amount.Should().Be(100m);
        charge.CurrencyCode.Should().Be("BRL");
        charge.Status.Should().Be(PixChargeStatus.Pending);
        charge.ProviderChargeId.Should().BeNull();
        charge.QrCodePayload.Should().BeNull();
        charge.IdempotencyKey.Should().NotBe(Guid.Empty);
        charge.ExpiresAtUtc.Should().BeNull();
        charge.ConfirmedAtUtc.Should().BeNull();
        charge.FailedAtUtc.Should().BeNull();
        charge.CreatedAtUtc.Should().Be(Now);
        charge.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_rejects_empty_LateCheckoutRequestId()
    {
        var act = () => PixCharge.Create(Guid.NewGuid(), TenantId, Guid.Empty, ReservationId, 100m, "BRL", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_empty_ReservationId()
    {
        var act = () => PixCharge.Create(Guid.NewGuid(), TenantId, LateCheckoutRequestId, Guid.Empty, 100m, "BRL", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_non_positive_amount(decimal amount)
    {
        var act = () => PixCharge.Create(Guid.NewGuid(), TenantId, LateCheckoutRequestId, ReservationId, amount, "BRL", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("")]
    public void Create_rejects_any_currency_other_than_BRL(string currencyCode)
    {
        var act = () => PixCharge.Create(Guid.NewGuid(), TenantId, LateCheckoutRequestId, ReservationId, 100m, currencyCode, Now);

        act.Should().Throw<ArgumentException>();
    }

    // ---- RecordProviderAcceptance ----

    [Fact]
    public void RecordProviderAcceptance_populates_provider_fields_and_stays_Pending()
    {
        var charge = CreateValid();
        var expiresAt = Now.AddMinutes(30);

        charge.RecordProviderAcceptance("provider-123", "00020126QRCODE", expiresAt, Now);

        charge.Status.Should().Be(PixChargeStatus.Pending);
        charge.ProviderChargeId.Should().Be("provider-123");
        charge.QrCodePayload.Should().Be("00020126QRCODE");
        charge.ExpiresAtUtc.Should().Be(expiresAt);
    }

    [Fact]
    public void RecordProviderAcceptance_throws_when_not_Pending()
    {
        var charge = CreateValid();
        charge.Fail(Now);

        var act = () => charge.RecordProviderAcceptance("provider-123", "qr", null, Now);

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- Fail ----

    [Fact]
    public void Fail_from_Pending_transitions_to_Failed()
    {
        var charge = CreateValid();

        charge.Fail(Now);

        charge.Status.Should().Be(PixChargeStatus.Failed);
        charge.FailedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Fail_is_a_no_op_when_already_Confirmed()
    {
        var charge = CreateValid();
        charge.Confirm(Now);

        charge.Fail(Now.AddMinutes(1));

        charge.Status.Should().Be(PixChargeStatus.Confirmed);
        charge.FailedAtUtc.Should().BeNull();
    }

    // ---- Confirm — full approved transition matrix (mandate item 10) ----

    [Fact]
    public void Confirm_from_Pending_transitions_to_Confirmed()
    {
        var charge = CreateValid();

        charge.Confirm(Now);

        charge.Status.Should().Be(PixChargeStatus.Confirmed);
        charge.ConfirmedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Confirm_duplicate_delivery_is_an_idempotent_no_op()
    {
        var charge = CreateValid();
        charge.Confirm(Now);

        charge.Confirm(Now.AddMinutes(5));

        charge.Status.Should().Be(PixChargeStatus.Confirmed);
        charge.ConfirmedAtUtc.Should().Be(Now, "the second confirmation must never overwrite the original timestamp");
    }

    [Fact]
    public void Confirm_from_Failed_forwards_to_Confirmed()
    {
        var charge = CreateValid();
        charge.Fail(Now);

        charge.Confirm(Now.AddMinutes(1));

        charge.Status.Should().Be(PixChargeStatus.Confirmed);
    }

    [Fact]
    public void Confirm_from_Expired_forwards_to_Confirmed()
    {
        var charge = CreateValid();
        SetStatusForTest(charge, PixChargeStatus.Expired);

        charge.Confirm(Now);

        charge.Status.Should().Be(PixChargeStatus.Confirmed);
    }

    [Fact]
    public void Confirm_from_Cancelled_throws_and_never_silently_decides()
    {
        // Nothing in this checkpoint's own code paths ever sets Cancelled —
        // this test proves the domain guard exists regardless, per the
        // mandate's explicit "PARE e reporte" instruction for this scenario.
        var charge = CreateValid();
        SetStatusForTest(charge, PixChargeStatus.Cancelled);

        var act = () => charge.Confirm(Now);

        act.Should().Throw<PixChargeCancelledConfirmationConflictException>();
    }

    private static void SetStatusForTest(PixCharge charge, PixChargeStatus status)
    {
        typeof(PixCharge).GetProperty(nameof(PixCharge.Status))!
            .SetValue(charge, status);
    }
}
