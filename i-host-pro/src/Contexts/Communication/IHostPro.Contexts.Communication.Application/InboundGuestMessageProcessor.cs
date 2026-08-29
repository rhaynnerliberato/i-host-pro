using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Contracts;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Reacts to <see cref="InboundGuestMessageReceived"/> (Fase 11, Checkpoint
/// 1 — Inbound Conversation Foundation): resolves the sender's phone to a
/// Reservation via <see cref="IReservationByGuestPhoneReader"/> (ADR-029,
/// synchronous exception #13), then creates/reuses the active
/// <see cref="Conversation"/> and persists the inbound <see cref="Message"/>.
/// NO AI/response logic exists here — this checkpoint stops at "the message
/// is safely on the ground," never sends anything back, never calls an
/// LLM (mandate item 4/28/29).
///
/// Resolution outcomes (mandate item 16, official decision):
/// <list type="bullet">
/// <item>0 candidates: no Conversation created, no Message persisted beyond
/// what dedupe needs — logged as <c>NoReservationResolved</c>, never a
/// failure, never a response.</item>
/// <item>Exactly 1 candidate: resolves automatically — Conversation
/// lookup/create, inbound Message persisted.</item>
/// <item>2+ candidates: NEVER auto-selected — logged as
/// <c>ReservationResolutionRequired</c>, no Conversation created; a future
/// checkpoint (CP2/CP5, once the AI Agent exists) does the conversational
/// disambiguation.</item>
/// </list>
///
/// Idempotency (mandate item 9/42): deduplicated by
/// <c>TenantId</c>/<c>Channel</c>/<c>ProviderMessageId</c>, reusing the SAME
/// mechanism every outbound processor already uses —
/// <see cref="IMessageRepository.GetByIdempotencyKeyAsync"/> plus the
/// existing unique index on <c>IdempotencyKey</c> (defense-in-depth). A
/// redelivered Meta webhook for an already-processed message is a silent,
/// zero-effect no-op, exactly like every outbound idempotency check in this
/// codebase — never a new dedup table.
/// </summary>
public sealed class InboundGuestMessageProcessor : IIntegrationEventHandler<InboundGuestMessageReceived>
{
    private const string IdempotencyKeyPrefix = "inbound";
    private const string NoReservationResolvedReason = "NoReservationResolved";
    private const string ReservationResolutionRequiredReason = "ReservationResolutionRequired";
    private const string UnsupportedMessagePlaceholder = "[UNSUPPORTED MESSAGE TYPE]";

    private readonly IReservationByGuestPhoneReader _reservationByGuestPhoneReader;
    private readonly IConversationResolver _conversationResolver;
    private readonly IMessageRepository _repository;
    private readonly IIntegrationEventCollector _collector;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly ILogger<InboundGuestMessageProcessor> _logger;

    public InboundGuestMessageProcessor(
        IReservationByGuestPhoneReader reservationByGuestPhoneReader,
        IConversationResolver conversationResolver,
        IMessageRepository repository,
        IIntegrationEventCollector collector,
        ICommunicationTransactionExecutor transactionExecutor,
        ILogger<InboundGuestMessageProcessor> logger)
    {
        _reservationByGuestPhoneReader = reservationByGuestPhoneReader;
        _conversationResolver = conversationResolver;
        _repository = repository;
        _collector = collector;
        _transactionExecutor = transactionExecutor;
        _logger = logger;
    }

    public async Task HandleAsync(InboundGuestMessageReceived @event, CancellationToken cancellationToken)
    {
        var idempotencyKey = BuildIdempotencyKey(@event.TenantId, @event.Channel, @event.ProviderMessageId);

        var existing = await _transactionExecutor.ExecuteAsync(
            () => _repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken), cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Communication {Trigger} skipped for tenant {TenantId}: {Result}",
                nameof(InboundGuestMessageReceived), @event.TenantId, "AlreadyProcessed");
            return;
        }

        var candidates = await _reservationByGuestPhoneReader.FindEligibleByGuestPhoneAsync(
            @event.TenantId, @event.SenderPhoneNormalized, cancellationToken);

        if (candidates.Count == 0)
        {
            _logger.LogInformation(
                "Communication {Trigger} outcome for tenant {TenantId}: {Result}",
                nameof(InboundGuestMessageReceived), @event.TenantId, NoReservationResolvedReason);
            return;
        }

        if (candidates.Count > 1)
        {
            _logger.LogInformation(
                "Communication {Trigger} outcome for tenant {TenantId}: {Result} ({CandidateCount} candidates)",
                nameof(InboundGuestMessageReceived), @event.TenantId, ReservationResolutionRequiredReason, candidates.Count);
            return;
        }

        var reservationId = candidates[0].ReservationId;

        var conversationId = await _conversationResolver.GetOrCreateActiveConversationIdAsync(
            @event.TenantId, reservationId, @event.Channel, @event.OccurredAtUtc, cancellationToken);

        var text = @event.MessageType == InboundGuestMessageType.Text ? @event.Text : UnsupportedMessagePlaceholder;

        var message = Message.CreateInbound(
            Guid.NewGuid(), @event.TenantId, conversationId, reservationId, @event.Channel,
            text, @event.ProviderMessageId, idempotencyKey, @event.OccurredAtUtc);

        await _transactionExecutor.ExecuteAsync(() =>
        {
            _repository.Add(message);

            // Fase 11, Checkpoint 2 (AI Agent Foundation) — Communication's
            // first published Integration Event, staged atomically with the
            // Message insert above (same outbox transaction). Deliberately
            // minimal: no message content, no PII beyond identifiers already
            // public within the tenant boundary — the AI Agent Bounded
            // Context resolves the actual (sanitized) content separately.
            _collector.Enqueue(new ConversationMessageReceived
            {
                TenantId = @event.TenantId,
                AggregateId = message.Id,
                AggregateType = "Message",
                CorrelationId = Guid.NewGuid(),
                ActorType = "System",
                ConversationId = conversationId,
                ReservationId = reservationId,
                MessageId = message.Id,
                OccurredAtUtc = @event.OccurredAtUtc,
            });

            return Task.FromResult(true);
        }, cancellationToken);

        _logger.LogInformation(
            "Communication {Trigger} outcome for tenant {TenantId} reservationId {ReservationId} conversationId {ConversationId} messageId {MessageId}: {Result}",
            nameof(InboundGuestMessageReceived), @event.TenantId, reservationId, conversationId, message.Id, "InboundMessagePersisted");
    }

    private static string BuildIdempotencyKey(Guid tenantId, string channel, string providerMessageId) =>
        $"{IdempotencyKeyPrefix}:{tenantId:D}:{channel}:{providerMessageId}";
}
