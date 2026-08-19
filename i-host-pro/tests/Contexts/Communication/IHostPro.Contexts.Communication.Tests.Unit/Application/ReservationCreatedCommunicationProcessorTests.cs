using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

public class ReservationCreatedCommunicationProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private const string ActiveTemplateContent = "Check-in em {{CheckInDate}}";

    private static ReservationCreated BuildEvent() => new()
    {
        TenantId = TenantId,
        AggregateId = ReservationId,
        AggregateType = "Reservation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        ReservationId = ReservationId,
        PropertyId = Guid.NewGuid(),
        Status = "confirmed",
        CheckInAt = new DateTimeOffset(2026, 8, 20, 15, 0, 0, TimeSpan.Zero),
    };

    private static ReservationCreatedCommunicationProcessor CreateProcessor(
        FakeTemplateReader templateReader, FakeReservationGuestContactReader contactReader,
        FakeMessageRepository repository, FakeOutboundMessageConnector connector) =>
        new(
            templateReader, contactReader, repository, new PassThroughCommunicationTransactionExecutor(),
            connector, new FixedTimeProvider(Now), NullLogger<ReservationCreatedCommunicationProcessor>.Instance);

    [Fact]
    public async Task HandleAsync_skips_when_a_message_already_exists_for_the_idempotency_key()
    {
        var existing = Message.Create(
            Guid.NewGuid(), TenantId, ReservationId, "WhatsApp", "RESERVATION_CONFIRMATION",
            null, "already sent", $"{TenantId:D}:{ReservationId:D}:RESERVATION_CONFIRMATION:WhatsApp", Now);
        var repository = FakeMessageRepository.WithExisting(existing);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, "+5511999998888")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty("a redelivered ReservationCreated must never create a second Message");
        connector.ReceivedDispatches.Should().BeEmpty("the connector must never be called for an already-processed reservation");
    }

    [Fact]
    public async Task HandleAsync_skips_when_no_active_template_exists()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(null),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, "+5511999998888")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_marks_Failed_with_no_contact_available_when_the_guest_has_no_phone_on_file()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, null)),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        connector.ReceivedDispatches.Should().BeEmpty("no connector call may be attempted without a destination");
        repository.AddedMessages.Should().ContainSingle();
        var message = repository.UpdatedMessages.Should().ContainSingle().Which;
        message.Status.Should().Be(MessageStatus.Failed);
        message.FailureReason.Should().Be("no_contact_available");
    }

    [Fact]
    public async Task HandleAsync_marks_Failed_when_the_reservation_itself_is_not_found_by_the_reader()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(null),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        var message = repository.UpdatedMessages.Should().ContainSingle().Which;
        message.Status.Should().Be(MessageStatus.Failed);
        message.FailureReason.Should().Be("no_contact_available");
    }

    [Fact]
    public async Task HandleAsync_dispatches_through_the_connector_and_marks_Sent_on_success()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, "+5511999998888")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        connector.ReceivedDispatches.Should().ContainSingle();
        connector.ReceivedDispatches[0].Destination.Should().Be("+5511999998888");
        connector.ReceivedDispatches[0].Content.Should().Be("Check-in em 2026-08-20");

        var message = repository.UpdatedMessages.Should().ContainSingle().Which;
        message.Status.Should().Be(MessageStatus.Sent);
    }

    [Fact]
    public async Task HandleAsync_never_persists_the_full_phone_only_a_masked_reference()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, "+5511999998888")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        var message = repository.AddedMessages.Should().ContainSingle().Which;
        message.DestinationMasked.Should().Be("**********8888");
        message.DestinationMasked.Should().NotContain("+5511999998888");
    }

    [Fact]
    public async Task HandleAsync_marks_Failed_with_the_connector_reason_when_the_connector_rejects()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Rejecting("provider_unavailable");
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, "+5511999998888")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        var message = repository.UpdatedMessages.Should().ContainSingle().Which;
        message.Status.Should().Be(MessageStatus.Failed);
        message.FailureReason.Should().Be("provider_unavailable");
    }

    [Fact]
    public async Task HandleAsync_marks_Failed_and_rethrows_when_the_connector_throws()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Throwing(new InvalidOperationException("network down"));
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, "+5511999998888")),
            repository, connector);

        var act = async () => await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var message = repository.UpdatedMessages.Should().ContainSingle().Which;
        message.Status.Should().Be(MessageStatus.Failed);
        message.FailureReason.Should().Be("connector_exception");
    }

    [Fact]
    public async Task HandleAsync_builds_the_idempotency_key_from_tenant_reservation_template_and_channel()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakeTemplateReader.Returning(new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent)),
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, "+5511999998888")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        var message = repository.AddedMessages.Should().ContainSingle().Which;
        message.IdempotencyKey.Should().Be($"{TenantId:D}:{ReservationId:D}:RESERVATION_CONFIRMATION:WhatsApp");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
