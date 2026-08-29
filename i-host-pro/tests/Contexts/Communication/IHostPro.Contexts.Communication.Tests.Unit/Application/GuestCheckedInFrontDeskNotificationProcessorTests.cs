using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 4 (Portaria Notification Foundation) mandate §37: for
/// each Front Desk intent, prove (a) a configured contact results in a real
/// Message dispatched to the FRONT DESK's phone (never the guest's), (b) a
/// missing contact is a deliberate no-op — zero Message, zero connector
/// call, and (c) no guest phone is ever persisted for this intent.
/// </summary>
public class GuestCheckedInFrontDeskNotificationProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private const string TemplateKey = "FRONT_DESK_GUEST_CHECKED_IN";
    private const string ActiveTemplateContent = "Hospede {{GuestName}} chegou em {{CheckedInAt}}";
    private const string GuestPhone = "+5511999998888";
    private const string FrontDeskPhone = "+5511977776666";

    private static GuestCheckedIn BuildEvent() => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "GuestStayOperation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        ReservationId = ReservationId,
        PropertyId = PropertyId,
        CheckedInAtUtc = Now,
    };

    private static GuestCheckedInFrontDeskNotificationProcessor CreateProcessor(
        FakeFrontDeskContactReader frontDeskReader, FakeTemplateReader templateReader,
        FakeReservationGuestContactReader guestContactReader, FakeMessageRepository repository, FakeOutboundMessageConnector connector) =>
        new(
            frontDeskReader, templateReader, guestContactReader, repository, FakeConversationResolver.Returning(ConversationId),
            new PassThroughCommunicationTransactionExecutor(),
            connector, new FixedTimeProvider(Now), NullLogger<GuestCheckedInFrontDeskNotificationProcessor>.Instance);

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
        connector.ReceivedDispatches.Should().ContainSingle()
            .Which.Destination.Should().Be(FrontDeskPhone, "the recipient must be the front desk contact, never the guest");
        connector.ReceivedDispatches[0].Destination.Should().NotBe(GuestPhone);
        repository.AddedMessages[0].Status.Should().Be(MessageStatus.Sent);
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

        repository.AddedMessages.Should().BeEmpty("no FrontDeskContact configured must never create a Message");
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
            Guid.NewGuid(), TenantId, ConversationId, ReservationId, "WhatsApp", TemplateKey,
            null, "already sent", $"{TenantId:D}:{ReservationId:D}:{TemplateKey}:WhatsApp", Now);
        var repository = FakeMessageRepository.WithExisting(existing);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeFrontDeskContactReader.Returning(new FrontDeskContactReadResult(Guid.NewGuid(), "Portaria Bloco A", FrontDeskPhone)),
            FakeTemplateReader.Returning(new ActiveTemplate(TemplateKey, ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty("a redelivered GuestCheckedIn must never create a second Message");
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_never_persists_the_full_front_desk_phone_only_a_masked_reference()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeFrontDeskContactReader.Returning(new FrontDeskContactReadResult(Guid.NewGuid(), "Portaria Bloco A", FrontDeskPhone)),
            FakeTemplateReader.Returning(new ActiveTemplate(TemplateKey, ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        var message = repository.AddedMessages.Single();
        message.DestinationMasked.Should().NotBeNull().And.NotContain(FrontDeskPhone);
        message.DestinationMasked.Should().EndWith("6666");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
