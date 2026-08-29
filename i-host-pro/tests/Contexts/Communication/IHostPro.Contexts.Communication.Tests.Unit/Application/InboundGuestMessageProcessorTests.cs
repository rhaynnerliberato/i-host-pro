using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Contracts;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

/// <summary>
/// Fase 11, Checkpoint 1 (Inbound Conversation Foundation) — proves the
/// official 0/1/N reservation-resolution decision (mandate item 16):
/// exactly one candidate resolves automatically, zero and multiple
/// candidates NEVER create a Conversation/Message and NEVER auto-select.
/// Also proves inbound idempotency (mandate item 9/42) and TEXT ONLY
/// handling (mandate item 24).
/// </summary>
public class InboundGuestMessageProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private const string SenderPhoneNormalized = "5511999998888";
    private const string ProviderMessageId = "wamid.INBOUND123";

    private static InboundGuestMessageReceived BuildEvent(
        InboundGuestMessageType messageType = InboundGuestMessageType.Text, string? text = "Olá, preciso de ajuda") => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "InboundGuestMessage",
        CorrelationId = Guid.NewGuid(),
        ActorType = "Integration",
        ProviderMessageId = ProviderMessageId,
        Channel = "WhatsApp",
        SenderPhoneNormalized = SenderPhoneNormalized,
        MessageType = messageType,
        Text = text,
        OccurredAtUtc = Now,
    };

    private static InboundGuestMessageProcessor CreateProcessor(
        FakeReservationByGuestPhoneReader reader, FakeMessageRepository repository, FakeIntegrationEventCollector? collector = null) =>
        new(
            reader, FakeConversationResolver.Returning(ConversationId), repository,
            collector ?? new FakeIntegrationEventCollector(),
            new PassThroughCommunicationTransactionExecutor(), NullLogger<InboundGuestMessageProcessor>.Instance);

    [Fact]
    public async Task HandleAsync_persists_a_Conversation_scoped_inbound_message_when_exactly_one_candidate_resolves()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var reader = FakeReservationByGuestPhoneReader.Returning(
            new ReservationCandidate(ReservationId, PropertyId, Now.AddDays(1), Now.AddDays(3)));
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(reader, repository, collector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().ContainSingle();
        var message = repository.AddedMessages[0];
        message.ReservationId.Should().Be(ReservationId);
        message.ConversationId.Should().Be(ConversationId);
        message.Direction.Should().Be(MessageDirection.Inbound);
        message.Status.Should().Be(MessageStatus.Received);
        message.RenderedContent.Should().Be("Olá, preciso de ajuda");
        message.ProviderMessageId.Should().Be(ProviderMessageId);

        collector.EnqueuedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ConversationMessageReceived>()
            .Which.Should().Match<ConversationMessageReceived>(e =>
                e.ConversationId == ConversationId && e.ReservationId == ReservationId && e.MessageId == message.Id);
    }

    [Fact]
    public async Task HandleAsync_creates_no_message_when_zero_candidates_resolve()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var reader = FakeReservationByGuestPhoneReader.Returning();
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(reader, repository, collector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty("0-reservation outcomes never create a Conversation/Message (mandate item 16)");
        collector.EnqueuedEvents.Should().BeEmpty("no Message means no ConversationMessageReceived to publish");
    }

    [Fact]
    public async Task HandleAsync_never_auto_selects_when_multiple_candidates_resolve()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var reader = FakeReservationByGuestPhoneReader.Returning(
            new ReservationCandidate(Guid.NewGuid(), PropertyId, Now.AddDays(1), Now.AddDays(3)),
            new ReservationCandidate(Guid.NewGuid(), PropertyId, Now.AddDays(5), Now.AddDays(7)));
        var processor = CreateProcessor(reader, repository);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty("N-reservation outcomes never auto-select one (mandate item 16) — disambiguation is a future checkpoint's job");
    }

    [Fact]
    public async Task HandleAsync_skips_when_the_same_provider_message_was_already_processed()
    {
        var idempotencyKey = $"inbound:{TenantId:D}:WhatsApp:{ProviderMessageId}";
        var existing = Message.CreateInbound(
            Guid.NewGuid(), TenantId, ConversationId, ReservationId, "WhatsApp",
            "already processed", ProviderMessageId, idempotencyKey, Now);
        var repository = FakeMessageRepository.WithExisting(existing);
        var reader = FakeReservationByGuestPhoneReader.Returning(
            new ReservationCandidate(ReservationId, PropertyId, Now.AddDays(1), Now.AddDays(3)));
        var processor = CreateProcessor(reader, repository);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty("a redelivered Meta webhook must never create a second inbound Message");
    }

    [Fact]
    public async Task HandleAsync_persists_a_placeholder_for_unsupported_message_types_never_null()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var reader = FakeReservationByGuestPhoneReader.Returning(
            new ReservationCandidate(ReservationId, PropertyId, Now.AddDays(1), Now.AddDays(3)));
        var processor = CreateProcessor(reader, repository);

        await processor.HandleAsync(BuildEvent(InboundGuestMessageType.Unsupported, text: null), CancellationToken.None);

        repository.AddedMessages.Should().ContainSingle();
        repository.AddedMessages[0].RenderedContent.Should().Be("[UNSUPPORTED MESSAGE TYPE]");
    }
}
