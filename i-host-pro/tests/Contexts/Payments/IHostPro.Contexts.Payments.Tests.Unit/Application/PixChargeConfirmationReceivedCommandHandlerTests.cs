using FluentAssertions;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Payments.Tests.Unit.Application;

public class PixChargeConfirmationReceivedCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid LateCheckoutRequestId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private static PixCharge CreateAcceptedCharge()
    {
        var charge = PixCharge.Create(Guid.NewGuid(), TenantId, LateCheckoutRequestId, ReservationId, 100m, "BRL", Now);
        charge.RecordProviderAcceptance("provider-abc", "qr", null, Now);
        return charge;
    }

    private static PixChargeConfirmationReceived BuildMessage(Guid pixChargeId) => new()
    {
        TenantId = TenantId,
        PixChargeId = pixChargeId,
        ConfirmedAtUtc = Now,
        CorrelationId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Confirms_a_Pending_charge_and_publishes_PixChargeConfirmed()
    {
        var charge = CreateAcceptedCharge();
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var collector = new FakeIntegrationEventCollector();
        var handler = new PixChargeConfirmationReceivedCommandHandler(
            repository, collector, new PassThroughPaymentsTransactionExecutor(),
            NullLogger<PixChargeConfirmationReceivedCommandHandler>.Instance);

        await handler.HandleAsync(BuildMessage(charge.Id), CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Confirmed);
        repository.UpdateCallCount.Should().Be(1);
        collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<PixChargeConfirmed>();
        var published = (PixChargeConfirmed)collector.EnqueuedEvents[0];
        published.LateCheckoutRequestId.Should().Be(LateCheckoutRequestId);
        published.ReservationId.Should().Be(ReservationId);
    }

    [Fact]
    public async Task Duplicate_confirmation_is_idempotent_and_publishes_nothing_a_second_time()
    {
        var charge = CreateAcceptedCharge();
        charge.Confirm(Now);
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var collector = new FakeIntegrationEventCollector();
        var handler = new PixChargeConfirmationReceivedCommandHandler(
            repository, collector, new PassThroughPaymentsTransactionExecutor(),
            NullLogger<PixChargeConfirmationReceivedCommandHandler>.Instance);

        await handler.HandleAsync(BuildMessage(charge.Id), CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Confirmed);
        collector.EnqueuedEvents.Should().BeEmpty("a duplicate confirmation must never publish a second PixChargeConfirmed");
    }

    [Fact]
    public async Task Unknown_charge_id_is_dropped_without_throwing()
    {
        var repository = new RecordingPixChargeRepository();
        var collector = new FakeIntegrationEventCollector();
        var handler = new PixChargeConfirmationReceivedCommandHandler(
            repository, collector, new PassThroughPaymentsTransactionExecutor(),
            NullLogger<PixChargeConfirmationReceivedCommandHandler>.Instance);

        var act = async () => await handler.HandleAsync(BuildMessage(Guid.NewGuid()), CancellationToken.None);

        await act.Should().NotThrowAsync();
        collector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Cross_tenant_charge_id_is_dropped_without_throwing()
    {
        var charge = CreateAcceptedCharge();
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var collector = new FakeIntegrationEventCollector();
        var handler = new PixChargeConfirmationReceivedCommandHandler(
            repository, collector, new PassThroughPaymentsTransactionExecutor(),
            NullLogger<PixChargeConfirmationReceivedCommandHandler>.Instance);

        var message = new PixChargeConfirmationReceived
        {
            TenantId = Guid.NewGuid(),
            PixChargeId = charge.Id,
            ConfirmedAtUtc = Now,
            CorrelationId = Guid.NewGuid(),
        };

        await handler.HandleAsync(message, CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Pending);
        collector.EnqueuedEvents.Should().BeEmpty();
    }
}
