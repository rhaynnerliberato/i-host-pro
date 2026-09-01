using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Handles <see cref="SendAgentResponseCommand"/> (Fase 11, Checkpoint 4).
/// Resolves the recipient/channel itself — <see cref="Conversation.Channel"/>
/// (never a model-supplied override) and <see cref="IReservationGuestContactReader"/>
/// (ADR-019/Exception #5, already approved, no new synchronous exception) —
/// mirrors <see cref="GuestAccessDeliveryProcessor"/>'s own Message-creation/
/// connector-call shape exactly, adapted to return a synchronous
/// <see cref="Result{TValue}"/> instead of reacting to an Integration Event.
///
/// Idempotency (CP4 mandate item 27): deterministic key from
/// <c>TenantId</c>/<c>AgentInteractionId</c>/the fixed template key/
/// <c>Channel</c> — one <see cref="AgentInteraction"/> (referenced only by
/// opaque id here — Communication never references AI Agent's own Domain)
/// produces at most one outbound Message. A repeated call with the same
/// <c>AgentInteractionId</c> returns the SAME already-created MessageId,
/// never a second row.
/// </summary>
public sealed class SendAgentResponseCommandHandler : ICommandHandler<SendAgentResponseCommand, SendAgentResponseResult>
{
    private const string TemplateKey = "AI_AGENT_RESPONSE";
    private static readonly IReadOnlyDictionary<string, string> EmptyTemplateVariables = new Dictionary<string, string>();

    private static readonly Error ConversationNotFoundError = new("ConversationNotFound", "ConversationNotFound");
    private static readonly Error GuestContactOrPhoneNotAvailableError = new("GuestContactOrPhoneNotAvailable", "GuestContactOrPhoneNotAvailable");
    private const string ConnectorExceptionFailureReason = "connector_exception";
    private const string ConnectorRejectedFailureReasonDefault = "connector_rejected";

    private readonly IConversationRepository _conversationRepository;
    private readonly IReservationGuestContactReader _guestContactReader;
    private readonly IMessageRepository _messageRepository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly IOutboundMessageConnector _connector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SendAgentResponseCommandHandler> _logger;

    public SendAgentResponseCommandHandler(
        IConversationRepository conversationRepository,
        IReservationGuestContactReader guestContactReader,
        IMessageRepository messageRepository,
        ICommunicationTransactionExecutor transactionExecutor,
        IOutboundMessageConnector connector,
        TimeProvider timeProvider,
        ILogger<SendAgentResponseCommandHandler> logger)
    {
        _conversationRepository = conversationRepository;
        _guestContactReader = guestContactReader;
        _messageRepository = messageRepository;
        _transactionExecutor = transactionExecutor;
        _connector = connector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<SendAgentResponseResult>> Handle(SendAgentResponseCommand command, CancellationToken cancellationToken)
    {
        var conversation = await _transactionExecutor.ExecuteAsync(
            () => _conversationRepository.GetByIdAsync(command.ConversationId, cancellationToken), cancellationToken);
        if (conversation is null)
        {
            _logger.LogWarning(
                "SendAgentResponse failed for tenant {TenantId} conversationId {ConversationId}: {Result}",
                command.TenantId, command.ConversationId, "ConversationNotFound");
            return Result.Failure<SendAgentResponseResult>(ConversationNotFoundError);
        }

        var guestContact = await _guestContactReader.GetGuestContactAsync(command.TenantId, command.ReservationId, cancellationToken);
        if (guestContact?.GuestPhone is null)
        {
            _logger.LogWarning(
                "SendAgentResponse failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                command.TenantId, command.ReservationId, "GuestContactOrPhoneNotAvailable");
            return Result.Failure<SendAgentResponseResult>(GuestContactOrPhoneNotAvailableError);
        }

        var idempotencyKey = BuildIdempotencyKey(command.TenantId, command.AgentInteractionId, TemplateKey, conversation.Channel);

        var existing = await _transactionExecutor.ExecuteAsync(
            () => _messageRepository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken), cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "SendAgentResponse skipped for tenant {TenantId} agentInteractionId {AgentInteractionId}: {Result}",
                command.TenantId, command.AgentInteractionId, "AlreadySent");
            return Result.Success(new SendAgentResponseResult(existing.Id));
        }

        var now = _timeProvider.GetUtcNow();
        var message = Message.Create(
            Guid.NewGuid(), command.TenantId, command.ConversationId, command.ReservationId, conversation.Channel,
            TemplateKey, Mask(guestContact.GuestPhone), command.Content, idempotencyKey, now);
        message.MarkQueued();

        await _transactionExecutor.ExecuteAsync(() =>
        {
            _messageRepository.Add(message);
            return Task.FromResult(true);
        }, cancellationToken);

        message.MarkSending();

        OutboundMessageDispatchResult dispatchResult;
        try
        {
            dispatchResult = await _connector.SendAsync(
                new OutboundMessageDispatch(
                    command.TenantId, message.Id, guestContact.GuestPhone, TemplateKey, EmptyTemplateVariables, command.Content, idempotencyKey),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SendAgentResponse connector threw for tenant {TenantId} agentInteractionId {AgentInteractionId} messageId {MessageId}",
                command.TenantId, command.AgentInteractionId, message.Id);

            message.MarkFailed(ConnectorExceptionFailureReason, _timeProvider.GetUtcNow());
            await UpdateAsync(message, cancellationToken);
            return Result.Failure<SendAgentResponseResult>(new Error(ConnectorExceptionFailureReason, ConnectorExceptionFailureReason));
        }

        if (dispatchResult.Success)
            message.MarkSent(_timeProvider.GetUtcNow(), dispatchResult.ProviderMessageId);
        else
            message.MarkFailed(dispatchResult.FailureReason ?? ConnectorRejectedFailureReasonDefault, _timeProvider.GetUtcNow());

        await UpdateAsync(message, cancellationToken);

        _logger.LogInformation(
            "SendAgentResponse for tenant {TenantId} agentInteractionId {AgentInteractionId}: messageId {MessageId} — result {Result}",
            command.TenantId, command.AgentInteractionId, message.Id, message.Status);

        if (!dispatchResult.Success)
            return Result.Failure<SendAgentResponseResult>(new Error(message.FailureReason!, message.FailureReason!));

        return Result.Success(new SendAgentResponseResult(message.Id));
    }

    private Task UpdateAsync(Message message, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            _messageRepository.Update(message);
            return Task.FromResult(true);
        }, cancellationToken);

    private static string BuildIdempotencyKey(Guid tenantId, Guid agentInteractionId, string templateKey, string channel) =>
        $"{tenantId:D}:{agentInteractionId:D}:{templateKey}:{channel}";

    private static string Mask(string phone) =>
        phone.Length <= 4
            ? new string('*', phone.Length)
            : new string('*', phone.Length - 4) + phone[^4..];
}
