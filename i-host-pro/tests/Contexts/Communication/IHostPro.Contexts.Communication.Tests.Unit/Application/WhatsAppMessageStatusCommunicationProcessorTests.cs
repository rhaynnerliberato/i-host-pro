using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

/// <summary>
/// Fase 9, Checkpoint 2.3.3 (ADR-022 item 14): covers the consumer's own
/// responsibilities beyond the aggregate's own transition matrix (already
/// covered by <c>MessageTests</c>) — lookup-by-ProviderMessageId, applying
/// only when Applied, and the explicit user decision (§22/§23/§28) that an
/// unknown ProviderMessageId is a retriable failure, never a silent no-op.
/// </summary>
public class WhatsAppMessageStatusCommunicationProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private const string ProviderMessageId = "wamid.HBgL...";

    private static WhatsAppMessageStatusChanged BuildEvent(
        WhatsAppMessageProviderStatus status, DateTimeOffset occurredAtUtc, int? providerErrorCode = null) => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "WhatsAppMessageStatus",
        CorrelationId = Guid.NewGuid(),
        ActorType = "Integration",
        ProviderMessageId = ProviderMessageId,
        Status = status,
        OccurredAtUtc = occurredAtUtc,
        ProviderErrorCode = providerErrorCode,
    };

    private static Message SentMessage()
    {
        var message = Message.Create(
            Guid.NewGuid(), TenantId, ConversationId, ReservationId, "WhatsApp", "RESERVATION_CONFIRMATION",
            "*******1234", "Olá, sua reserva foi confirmada.", "idempotency-key", Now);
        message.MarkQueued();
        message.MarkSending();
        message.MarkSent(Now, ProviderMessageId);
        return message;
    }

    private static WhatsAppMessageStatusCommunicationProcessor CreateProcessor(FakeMessageRepository repository) =>
        new(repository, new PassThroughCommunicationTransactionExecutor(),
            NullLogger<WhatsAppMessageStatusCommunicationProcessor>.Instance);

    [Fact]
    public async Task HandleAsync_applies_a_forward_status_and_persists_it()
    {
        var existing = SentMessage();
        var repository = FakeMessageRepository.WithExisting(existing);
        var processor = CreateProcessor(repository);

        await processor.HandleAsync(BuildEvent(WhatsAppMessageProviderStatus.Delivered, Now.AddSeconds(1)), CancellationToken.None);

        existing.Status.Should().Be(MessageStatus.Delivered);
        repository.UpdatedMessages.Should().ContainSingle().Which.Should().Be(existing);
    }

    [Fact]
    public async Task HandleAsync_never_persists_a_duplicate_status()
    {
        var existing = SentMessage();
        existing.ApplyProviderStatus(WhatsAppProviderStatus.Delivered, Now.AddSeconds(1));
        var repository = FakeMessageRepository.WithExisting(existing);
        var processor = CreateProcessor(repository);

        await processor.HandleAsync(BuildEvent(WhatsAppMessageProviderStatus.Delivered, Now.AddSeconds(2)), CancellationToken.None);

        existing.Status.Should().Be(MessageStatus.Delivered);
        repository.UpdatedMessages.Should().BeEmpty("a Duplicate classification must never trigger a write");
    }

    [Fact]
    public async Task HandleAsync_never_persists_a_regressive_status()
    {
        var existing = SentMessage();
        existing.ApplyProviderStatus(WhatsAppProviderStatus.Read, Now.AddSeconds(1));
        var repository = FakeMessageRepository.WithExisting(existing);
        var processor = CreateProcessor(repository);

        await processor.HandleAsync(BuildEvent(WhatsAppMessageProviderStatus.Failed, Now.AddSeconds(2)), CancellationToken.None);

        existing.Status.Should().Be(MessageStatus.Read, "Read is terminal for Failed purposes");
        repository.UpdatedMessages.Should().BeEmpty("a Regression classification must never trigger a write");
    }

    [Fact]
    public async Task HandleAsync_throws_when_no_Message_matches_the_ProviderMessageId()
    {
        // Explicit user decision (Checkpoint 2.3.3, §22/§23/§28): a retriable
        // failure, never a silent permanent no-op — so Wolverine's normal
        // retry/DLQ policy handles both the transient commit race and a
        // genuinely orphaned id.
        var repository = FakeMessageRepository.WithExisting(null);
        var processor = CreateProcessor(repository);

        var act = () => processor.HandleAsync(BuildEvent(WhatsAppMessageProviderStatus.Delivered, Now.AddSeconds(1)), CancellationToken.None);

        await act.Should().ThrowAsync<WhatsAppMessageNotYetAvailableException>();
        repository.UpdatedMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_maps_ProviderErrorCode_through_to_the_aggregate()
    {
        var existing = SentMessage();
        var repository = FakeMessageRepository.WithExisting(existing);
        var processor = CreateProcessor(repository);

        await processor.HandleAsync(BuildEvent(WhatsAppMessageProviderStatus.Failed, Now.AddSeconds(1), 131026), CancellationToken.None);

        existing.Status.Should().Be(MessageStatus.Failed);
        existing.FailureReason.Should().Be("provider_error_131026");
    }
}
