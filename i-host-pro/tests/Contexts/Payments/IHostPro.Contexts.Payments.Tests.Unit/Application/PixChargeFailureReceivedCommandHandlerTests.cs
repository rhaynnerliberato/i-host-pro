using FluentAssertions;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Payments.Tests.Unit.Application;

public class PixChargeFailureReceivedCommandHandlerTests
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

    private static PixChargeFailureReceived BuildMessage(Guid pixChargeId, string? failureCode = null) => new()
    {
        TenantId = TenantId,
        PixChargeId = pixChargeId,
        FailureCode = failureCode,
        OccurredAtUtc = Now,
        CorrelationId = Guid.NewGuid(),
    };

    private static PixChargeFailureReceivedCommandHandler CreateHandler(RecordingPixChargeRepository repository) =>
        new(repository, new PassThroughPaymentsTransactionExecutor(), NullLogger<PixChargeFailureReceivedCommandHandler>.Instance);

    [Fact]
    public async Task Fails_a_Pending_charge()
    {
        var charge = CreateAcceptedCharge();
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var handler = CreateHandler(repository);

        await handler.HandleAsync(BuildMessage(charge.Id, "pix_timeout"), CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Failed);
        repository.UpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_failure_delivery_is_an_idempotent_no_op()
    {
        var charge = CreateAcceptedCharge();
        var repository = new RecordingPixChargeRepository();
        repository.Add(charge);
        var handler = CreateHandler(repository);

        await handler.HandleAsync(BuildMessage(charge.Id), CancellationToken.None);
        await handler.HandleAsync(BuildMessage(charge.Id), CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Failed);
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

        var message = new PixChargeFailureReceived
        {
            TenantId = Guid.NewGuid(),
            PixChargeId = charge.Id,
            OccurredAtUtc = Now,
            CorrelationId = Guid.NewGuid(),
        };

        await handler.HandleAsync(message, CancellationToken.None);

        charge.Status.Should().Be(PixChargeStatus.Pending);
    }

    /// <summary>Checkpoint 5.1 mandate item 8: a real confirmation always takes precedence — a late failure signal for an already-Confirmed charge must never regress its status.</summary>
    [Fact]
    public async Task Confirmed_charge_plus_failure_received_is_a_no_op()
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
