using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

/// <summary>Mirrors <see cref="GuestCheckedInFrontDeskNotificationProcessorTests"/> exactly — see its doc comment.</summary>
public class LateCheckoutApprovedFrontDeskNotificationProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private const string TemplateKey = "FRONT_DESK_LATE_CHECKOUT_APPROVED";
    private const string ActiveTemplateContent = "Checkout tardio de {{GuestName}} até {{ApprovedCheckOutAt}}";
    private const string GuestPhone = "+5511999998888";
    private const string FrontDeskPhone = "+5511977776666";

    private static LateCheckoutApproved BuildEvent(bool updatesCleaning = false) => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "LateCheckoutRequest",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        ReservationId = ReservationId,
        PropertyId = PropertyId,
        ApprovedCheckOutAt = Now,
        UpdatesCleaning = updatesCleaning,
    };

    private static LateCheckoutApprovedFrontDeskNotificationProcessor CreateProcessor(
        FakeFrontDeskContactReader frontDeskReader, FakeTemplateReader templateReader,
        FakeReservationGuestContactReader guestContactReader, FakeMessageRepository repository, FakeOutboundMessageConnector connector) =>
        new(
            frontDeskReader, templateReader, guestContactReader, repository, new PassThroughCommunicationTransactionExecutor(),
            connector, new FixedTimeProvider(Now), NullLogger<LateCheckoutApprovedFrontDeskNotificationProcessor>.Instance);

    [Fact]
    public async Task HandleAsync_dispatches_to_the_front_desk_phone_never_the_guest_phone()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeFrontDeskContactReader.Returning(new FrontDeskContactReadResult(Guid.NewGuid(), "Portaria Bloco A", FrontDeskPhone)),
            FakeTemplateReader.Returning(new ActiveTemplate(TemplateKey, ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().ContainSingle();
        connector.ReceivedDispatches.Should().ContainSingle().Which.Destination.Should().Be(FrontDeskPhone);
    }

    [Fact]
    public async Task HandleAsync_dispatches_regardless_of_UpdatesCleaning_which_is_Housekeepings_own_gate()
    {
        // Confirms this processor never reads UpdatesCleaning — that field
        // gates ONLY Housekeeping's own reactor (a separate, independent
        // consumer of the same event, ADR-020), never the front desk
        // notification itself.
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeFrontDeskContactReader.Returning(new FrontDeskContactReadResult(Guid.NewGuid(), "Portaria Bloco A", FrontDeskPhone)),
            FakeTemplateReader.Returning(new ActiveTemplate(TemplateKey, ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(updatesCleaning: false), CancellationToken.None);

        repository.AddedMessages.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_is_a_deliberate_no_op_when_no_front_desk_contact_is_configured()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeFrontDeskContactReader.Returning(null),
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
            FakeFrontDeskContactReader.Returning(new FrontDeskContactReadResult(Guid.NewGuid(), "Portaria Bloco A", FrontDeskPhone)),
            FakeTemplateReader.Returning(null),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_skips_when_a_message_already_exists_for_the_idempotency_key()
    {
        var existing = Message.Create(
            Guid.NewGuid(), TenantId, ReservationId, "WhatsApp", TemplateKey,
            null, "already sent", $"{TenantId:D}:{ReservationId:D}:{TemplateKey}:WhatsApp", Now);
        var repository = FakeMessageRepository.WithExisting(existing);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeFrontDeskContactReader.Returning(new FrontDeskContactReadResult(Guid.NewGuid(), "Portaria Bloco A", FrontDeskPhone)),
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
