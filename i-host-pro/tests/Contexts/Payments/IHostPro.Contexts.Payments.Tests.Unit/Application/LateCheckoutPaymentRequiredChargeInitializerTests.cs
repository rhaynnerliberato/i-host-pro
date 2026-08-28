using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Payments.Tests.Unit.Application;

public class LateCheckoutPaymentRequiredChargeInitializerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid LateCheckoutRequestId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private static LateCheckoutPaymentRequired BuildEvent(decimal amount = 100m, string currencyCode = "BRL") => new()
    {
        TenantId = TenantId,
        AggregateId = LateCheckoutRequestId,
        AggregateType = "LateCheckoutRequest",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        LateCheckoutRequestId = LateCheckoutRequestId,
        ReservationId = ReservationId,
        Amount = amount,
        CurrencyCode = currencyCode,
        OccurredAtUtc = Now,
    };

    [Fact]
    public async Task Provider_Accepted_creates_a_charge_with_provider_data_and_publishes_PixChargeCreated()
    {
        var reader = FakePixChargeReader.WithActiveCharge(null);
        var repository = new RecordingPixChargeRepository();
        var provider = ConfigurablePixProvider.Accepting("provider-abc", "00020126FAKEQR", Now.AddMinutes(30));
        var collector = new FakeIntegrationEventCollector();
        var handler = new LateCheckoutPaymentRequiredChargeInitializer(
            reader, repository, provider, collector, new PassThroughPaymentsTransactionExecutor(),
            new FixedTimeProvider(Now), NullLogger<LateCheckoutPaymentRequiredChargeInitializer>.Instance);

        await handler.HandleAsync(BuildEvent(150m), CancellationToken.None);

        repository.AddedCharges.Should().ContainSingle();
        var charge = repository.AddedCharges[0];
        charge.Status.Should().Be(PixChargeStatus.Pending);
        charge.Amount.Should().Be(150m);
        charge.CurrencyCode.Should().Be("BRL");
        charge.ProviderChargeId.Should().Be("provider-abc");
        charge.QrCodePayload.Should().Be("00020126FAKEQR");

        collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<PixChargeCreated>();
        var published = (PixChargeCreated)collector.EnqueuedEvents[0];
        published.LateCheckoutRequestId.Should().Be(LateCheckoutRequestId);
        published.ReservationId.Should().Be(ReservationId);
        published.AggregateId.Should().Be(charge.Id);
    }

    [Fact]
    public async Task Provider_Rejected_marks_the_charge_Failed_and_publishes_nothing()
    {
        var reader = FakePixChargeReader.WithActiveCharge(null);
        var repository = new RecordingPixChargeRepository();
        var provider = ConfigurablePixProvider.Rejecting("insufficient_data");
        var collector = new FakeIntegrationEventCollector();
        var handler = new LateCheckoutPaymentRequiredChargeInitializer(
            reader, repository, provider, collector, new PassThroughPaymentsTransactionExecutor(),
            new FixedTimeProvider(Now), NullLogger<LateCheckoutPaymentRequiredChargeInitializer>.Instance);

        await handler.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedCharges.Should().ContainSingle();
        repository.AddedCharges[0].Status.Should().Be(PixChargeStatus.Failed);
        collector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_technical_failure_propagates_so_Wolverine_can_redeliver()
    {
        var reader = FakePixChargeReader.WithActiveCharge(null);
        var repository = new RecordingPixChargeRepository();
        var provider = ConfigurablePixProvider.ThrowingTechnicalFailure(new HttpRequestException("network unreachable"));
        var collector = new FakeIntegrationEventCollector();
        var handler = new LateCheckoutPaymentRequiredChargeInitializer(
            reader, repository, provider, collector, new PassThroughPaymentsTransactionExecutor(),
            new FixedTimeProvider(Now), NullLogger<LateCheckoutPaymentRequiredChargeInitializer>.Instance);

        var act = async () => await handler.HandleAsync(BuildEvent(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Redelivered_event_with_an_existing_active_charge_is_a_no_op()
    {
        var existingChargeId = Guid.NewGuid();
        var reader = FakePixChargeReader.WithActiveCharge(existingChargeId);
        var repository = new RecordingPixChargeRepository();
        var provider = ConfigurablePixProvider.Accepting("provider-abc", "qr", null);
        var collector = new FakeIntegrationEventCollector();
        var handler = new LateCheckoutPaymentRequiredChargeInitializer(
            reader, repository, provider, collector, new PassThroughPaymentsTransactionExecutor(),
            new FixedTimeProvider(Now), NullLogger<LateCheckoutPaymentRequiredChargeInitializer>.Instance);

        await handler.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedCharges.Should().BeEmpty();
        provider.CallCount.Should().Be(0, "the provider must never be called for an already-active charge");
        collector.EnqueuedEvents.Should().BeEmpty();
    }
}
