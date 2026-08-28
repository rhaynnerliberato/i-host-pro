using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

/// <summary>Mirrors <see cref="LateCheckoutApprovedFrontDeskNotificationProcessorTests"/> exactly — see its doc comment.</summary>
public class PixChargeCreatedDeliveryProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PixChargeId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private const string TemplateKey = "LATE_CHECKOUT_PIX_PAYMENT";
    private const string ActiveTemplateContent = "Olá {{GuestName}}, o valor de {{Amount}} pode ser pago via PIX: {{PixCode}}";
    private const string GuestPhone = "+5511999998888";
    private const string QrCodePayload = "00020126FAKEQR";

    private static PixChargeCreated BuildEvent() => new()
    {
        TenantId = TenantId,
        AggregateId = PixChargeId,
        AggregateType = "PixCharge",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        LateCheckoutRequestId = Guid.NewGuid(),
        ReservationId = ReservationId,
    };

    private static PixChargeCreatedDeliveryProcessor CreateProcessor(
        FakePixChargeDeliveryReader deliveryReader, FakeTemplateReader templateReader,
        FakeReservationGuestContactReader guestContactReader, FakeMessageRepository repository, FakeOutboundMessageConnector connector) =>
        new(
            deliveryReader, templateReader, guestContactReader, repository, new PassThroughCommunicationTransactionExecutor(),
            connector, new FixedTimeProvider(Now), NullLogger<PixChargeCreatedDeliveryProcessor>.Instance);

    [Fact]
    public async Task HandleAsync_dispatches_to_the_guest_phone_and_renders_the_QR_payload_into_the_message()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePixChargeDeliveryReader.Returning(new PixChargeDeliveryReadResult(PixChargeId, QrCodePayload, 150m, "BRL", null)),
            FakeTemplateReader.Returning(new ActiveTemplate(TemplateKey, ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().ContainSingle();
        connector.ReceivedDispatches.Should().ContainSingle().Which.Destination.Should().Be(GuestPhone);
        connector.ReceivedDispatches[0].Content.Should().Contain(QrCodePayload);
    }

    [Fact]
    public async Task HandleAsync_is_a_deliberate_no_op_when_the_charge_has_no_delivery_data_yet()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePixChargeDeliveryReader.Returning(null),
            FakeTemplateReader.Returning(new ActiveTemplate(TemplateKey, ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_skips_when_no_active_template_exists()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePixChargeDeliveryReader.Returning(new PixChargeDeliveryReadResult(PixChargeId, QrCodePayload, 150m, "BRL", null)),
            FakeTemplateReader.Returning(null),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_skips_when_the_guest_has_no_phone_on_file()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePixChargeDeliveryReader.Returning(new PixChargeDeliveryReadResult(PixChargeId, QrCodePayload, 150m, "BRL", null)),
            FakeTemplateReader.Returning(new ActiveTemplate(TemplateKey, ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, null, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_skips_when_a_message_already_exists_for_the_idempotency_key()
    {
        var idempotencyKey = $"{TenantId:D}:{PixChargeId:D}:{TemplateKey}:WhatsApp";
        var existing = Message.Create(Guid.NewGuid(), TenantId, ReservationId, "WhatsApp", TemplateKey, null, "already sent", idempotencyKey, Now);
        var repository = FakeMessageRepository.WithExisting(existing);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePixChargeDeliveryReader.Returning(new PixChargeDeliveryReadResult(PixChargeId, QrCodePayload, 150m, "BRL", null)),
            FakeTemplateReader.Returning(new ActiveTemplate(TemplateKey, ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
