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
public class EarlyCheckinApprovedFrontDeskNotificationProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private const string TemplateKey = "FRONT_DESK_EARLY_CHECKIN_APPROVED";
    private const string ActiveTemplateContent = "Check-in antecipado de {{GuestName}} para {{ApprovedCheckInAt}}";
    private const string GuestPhone = "+5511999998888";
    private const string FrontDeskPhone = "+5511977776666";

    private static EarlyCheckinApproved BuildEvent() => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "EarlyCheckInRequest",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        ReservationId = ReservationId,
        PropertyId = PropertyId,
        ApprovedCheckInAt = Now,
    };

    private static EarlyCheckinApprovedFrontDeskNotificationProcessor CreateProcessor(
        FakeFrontDeskContactReader frontDeskReader, FakeTemplateReader templateReader,
        FakeReservationGuestContactReader guestContactReader, FakeMessageRepository repository, FakeOutboundMessageConnector connector) =>
        new(
            frontDeskReader, templateReader, guestContactReader, repository, new PassThroughCommunicationTransactionExecutor(),
            connector, new FixedTimeProvider(Now), NullLogger<EarlyCheckinApprovedFrontDeskNotificationProcessor>.Instance);

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
