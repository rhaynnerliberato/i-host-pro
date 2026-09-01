using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Communication.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Handles <see cref="SendHumanHandoffNotificationCommand"/> (Fase 11,
/// Checkpoint 6). Resolves the recipient itself — the Tenant's ACTIVE
/// <see cref="AdministratorNotificationContact"/> (CP6 mandate item 19/25:
/// Communication owns recipient/channel/destination end-to-end, never
/// accepted from the caller) — mirrors <see cref="SendAgentResponseCommandHandler"/>'s
/// own Message-creation/connector-call shape exactly.
///
/// Idempotency (CP6 mandate item 28): deterministic key from
/// <c>AI_HUMAN_HANDOFF</c>/<see cref="SendHumanHandoffNotificationCommand.AgentHumanHandoffId"/> —
/// one <see cref="Domain.AgentHumanHandoff"/> (Communication never references
/// AI Agent's own Domain — referenced only by opaque id here) produces at
/// most one outbound Message. A repeated call with the same
/// <c>AgentHumanHandoffId</c> returns the SAME already-created MessageId,
/// never a second row (CP6 mandate item 30 — at most 1 retry, same key).
/// </summary>
public sealed class SendHumanHandoffNotificationCommandHandler
    : ICommandHandler<SendHumanHandoffNotificationCommand, SendHumanHandoffNotificationResult>
{
    private const string TemplateKey = "AI_HUMAN_HANDOFF_NOTIFICATION";
    private static readonly IReadOnlyDictionary<string, string> EmptyTemplateVariables = new Dictionary<string, string>();

    private static readonly Error ConversationNotFoundError = new("ConversationNotFound", "ConversationNotFound");
    private static readonly Error NoActiveAdministratorNotificationContactError =
        new("NoActiveAdministratorNotificationContact", "NoActiveAdministratorNotificationContact");
    private const string ConnectorExceptionFailureReason = "connector_exception";
    private const string ConnectorRejectedFailureReasonDefault = "connector_rejected";

    private readonly IConversationRepository _conversationRepository;
    private readonly IAdministratorNotificationContactRepository _administratorContactRepository;
    private readonly IMessageRepository _messageRepository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly IOutboundMessageConnector _connector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SendHumanHandoffNotificationCommandHandler> _logger;

    public SendHumanHandoffNotificationCommandHandler(
        IConversationRepository conversationRepository,
        IAdministratorNotificationContactRepository administratorContactRepository,
        IMessageRepository messageRepository,
        ICommunicationTransactionExecutor transactionExecutor,
        IOutboundMessageConnector connector,
        TimeProvider timeProvider,
        ILogger<SendHumanHandoffNotificationCommandHandler> logger)
    {
        _conversationRepository = conversationRepository;
        _administratorContactRepository = administratorContactRepository;
        _messageRepository = messageRepository;
        _transactionExecutor = transactionExecutor;
        _connector = connector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<SendHumanHandoffNotificationResult>> Handle(
        SendHumanHandoffNotificationCommand command, CancellationToken cancellationToken)
    {
        var conversation = await _transactionExecutor.ExecuteAsync(
            () => _conversationRepository.GetByIdAsync(command.ConversationId, cancellationToken), cancellationToken);
        if (conversation is null)
        {
            _logger.LogWarning(
                "SendHumanHandoffNotification failed for tenant {TenantId} conversationId {ConversationId}: {Result}",
                command.TenantId, command.ConversationId, "ConversationNotFound");
            return Result.Failure<SendHumanHandoffNotificationResult>(ConversationNotFoundError);
        }

        var administratorContact = await _transactionExecutor.ExecuteAsync(
            () => _administratorContactRepository.GetActiveByTenantIdAsync(command.TenantId, cancellationToken), cancellationToken);
        if (administratorContact is null)
        {
            _logger.LogWarning(
                "SendHumanHandoffNotification failed for tenant {TenantId} agentHumanHandoffId {AgentHumanHandoffId}: {Result}",
                command.TenantId, command.AgentHumanHandoffId, "NoActiveAdministratorNotificationContact");
            return Result.Failure<SendHumanHandoffNotificationResult>(NoActiveAdministratorNotificationContactError);
        }

        var idempotencyKey = BuildIdempotencyKey(command.TenantId, command.AgentHumanHandoffId);

        var existing = await _transactionExecutor.ExecuteAsync(
            () => _messageRepository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken), cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "SendHumanHandoffNotification skipped for tenant {TenantId} agentHumanHandoffId {AgentHumanHandoffId}: {Result}",
                command.TenantId, command.AgentHumanHandoffId, "AlreadySent");
            return Result.Success(new SendHumanHandoffNotificationResult(existing.Id));
        }

        var now = _timeProvider.GetUtcNow();
        var content = BuildContent(command.ReasonCode, command.ReservationId, now);
        var message = Message.Create(
            Guid.NewGuid(), command.TenantId, command.ConversationId, command.ReservationId, conversation.Channel,
            TemplateKey, Mask(administratorContact.DestinationPhone), content, idempotencyKey, now);
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
                    command.TenantId, message.Id, administratorContact.DestinationPhone, TemplateKey, EmptyTemplateVariables, content, idempotencyKey),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SendHumanHandoffNotification connector threw for tenant {TenantId} agentHumanHandoffId {AgentHumanHandoffId} messageId {MessageId}",
                command.TenantId, command.AgentHumanHandoffId, message.Id);

            message.MarkFailed(ConnectorExceptionFailureReason, _timeProvider.GetUtcNow());
            await UpdateAsync(message, cancellationToken);
            return Result.Failure<SendHumanHandoffNotificationResult>(new Error(ConnectorExceptionFailureReason, ConnectorExceptionFailureReason));
        }

        if (dispatchResult.Success)
            message.MarkSent(_timeProvider.GetUtcNow(), dispatchResult.ProviderMessageId);
        else
            message.MarkFailed(dispatchResult.FailureReason ?? ConnectorRejectedFailureReasonDefault, _timeProvider.GetUtcNow());

        await UpdateAsync(message, cancellationToken);

        _logger.LogInformation(
            "SendHumanHandoffNotification for tenant {TenantId} agentHumanHandoffId {AgentHumanHandoffId}: messageId {MessageId} — result {Result}",
            command.TenantId, command.AgentHumanHandoffId, message.Id, message.Status);

        if (!dispatchResult.Success)
            return Result.Failure<SendHumanHandoffNotificationResult>(new Error(message.FailureReason!, message.FailureReason!));

        return Result.Success(new SendHumanHandoffNotificationResult(message.Id));
    }

    private Task UpdateAsync(Message message, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            _messageRepository.Update(message);
            return Task.FromResult(true);
        }, cancellationToken);

    private static string BuildIdempotencyKey(Guid tenantId, Guid agentHumanHandoffId) =>
        $"{tenantId:D}:AI_HUMAN_HANDOFF:{agentHumanHandoffId:D}";

    /// <summary>Sanitized content only (CP6 mandate item 27) — ReasonCode, an opaque Reservation reference, and a timestamp; never a raw guest message, GuestName, GuestPhone, credential, QR, or tool output.</summary>
    private static string BuildContent(string reasonCode, Guid reservationId, DateTimeOffset now) =>
        $"Solicitação de atendimento humano. Motivo: {reasonCode}. Reserva: {reservationId:D}. Registrado em: {now:O}.";

    private static string Mask(string phone) =>
        phone.Length <= 4
            ? new string('*', phone.Length)
            : new string('*', phone.Length - 4) + phone[^4..];
}
