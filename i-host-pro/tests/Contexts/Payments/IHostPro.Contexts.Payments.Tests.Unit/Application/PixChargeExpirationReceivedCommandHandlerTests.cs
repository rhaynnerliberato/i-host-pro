using FluentAssertions;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Payments.Tests.Unit.Application;

public class PixChargeExpirationReceivedCommandHandlerTests
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

    private static PixChargeExpirationReceived BuildMessage(Guid pixChargeId) => new()
    {
        TenantId = TenantId,
        PixChargeId = pixChargeId,
        ExpiredAtUtc = Now,
        CorrelationId = Guid.NewGuid(),
    };

    private static PixChargeExpirationReceivedCommandHandler CreateHandler(RecordingPixChargeRepository repository) =>
        new(repository, new PassThroughPaymentsTransactionExecutor(), NullLogger<PixChargeExpirationReceivedCommandHandler>.Instance);

    [Fact]
    public async Task Expires_a_Pending_charge()
    {
        var charge = CreateAcceptedCharge();
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var handler = CreateHandler(repository);

        await handler.HandleAsync(BuildMessage(charge.Id), CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Expired);
        repository.UpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_expiration_delivery_is_an_idempotent_no_op()
    {
        var charge = CreateAcceptedCharge();
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var handler = CreateHandler(repository);

        await handler.HandleAsync(BuildMessage(charge.Id), CancellationToken.None);
        await handler.HandleAsync(BuildMessage(charge.Id), CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Expired);
    }

    [Fact]
    public async Task Unknown_charge_id_is_dropped_without_throwing()
    {
        var repository = new RecordingPixChargeRepository();
        var handler = CreateHandler(repository);

        var act = async () => await handler.HandleAsync(BuildMessage(Guid.NewGuid()), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Cross_tenant_charge_id_is_dropped_without_throwing()
    {
        var charge = CreateAcceptedCharge();
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var handler = CreateHandler(repository);

        var message = new PixChargeExpirationReceived
        {
            TenantId = Guid.NewGuid(),
            PixChargeId = charge.Id,
            ExpiredAtUtc = Now,
            CorrelationId = Guid.NewGuid(),
        };

        await handler.HandleAsync(message, CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Pending);
    }

    /// <summary>Checkpoint 5.1 mandate item 8: a real confirmation always takes precedence — a late expiration signal for an already-Confirmed charge must never regress its status.</summary>
    [Fact]
    public async Task Confirmed_charge_plus_expiration_received_is_a_no_op()
    {
        var charge = CreateAcceptedCharge();
        charge.Confirm(Now);
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var handler = CreateHandler(repository);

        await handler.HandleAsync(BuildMessage(charge.Id), CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Confirmed);
    }
}
