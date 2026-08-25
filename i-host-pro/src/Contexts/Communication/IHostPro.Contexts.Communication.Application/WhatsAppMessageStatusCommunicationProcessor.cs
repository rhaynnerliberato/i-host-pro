using System.Diagnostics;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Reacts to <see cref="WhatsAppMessageStatusChanged"/> (Fase 9, Checkpoint
/// 2.3.3, ADR-022 item 14) — looks up the <see cref="Message"/> by
/// <see cref="WhatsAppMessageStatusChanged.ProviderMessageId"/>, applies the
/// status idempotently via <see cref="Message.ApplyProviderStatus"/>, and
/// persists only when it actually changed anything. Never calls
/// ExternalIntegrations' runtime (mandate §14) — only the event's own
/// already-resolved fields are used.
///
/// Implements <see cref="IIntegrationEventHandler{TEvent}"/> directly, same
/// as <see cref="ReservationCreatedCommunicationProcessor"/> — resolved
/// exclusively from <see cref="ICommunicationMessageExecutionScope"/>'s own
/// child DI scope, never by Wolverine's own per-message resolution.
/// </summary>
public sealed class WhatsAppMessageStatusCommunicationProcessor : IIntegrationEventHandler<WhatsAppMessageStatusChanged>
{
    private const string Applied = "WhatsAppMessageStatusApplied";
    private const string AppliedFailed = "WhatsAppMessageStatusFailed";
    private const string Duplicate = "WhatsAppMessageStatusDuplicate";
    private const string Regression = "WhatsAppMessageStatusRegression";
    private const string UnknownMessage = "WhatsAppMessageStatusUnknownMessage";

    private readonly IMessageRepository _repository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly ILogger<WhatsAppMessageStatusCommunicationProcessor> _logger;

    public WhatsAppMessageStatusCommunicationProcessor(
        IMessageRepository repository,
        ICommunicationTransactionExecutor transactionExecutor,
        ILogger<WhatsAppMessageStatusCommunicationProcessor> logger)
    {
        _repository = repository;
        _transactionExecutor = transactionExecutor;
        _logger = logger;
    }

    public async Task HandleAsync(WhatsAppMessageStatusChanged @event, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var message = await _transactionExecutor.ExecuteAsync(
            () => _repository.GetByProviderMessageIdAsync(@event.ProviderMessageId, cancellationToken), cancellationToken);

        if (message is null)
        {
            // Per explicit decision (Checkpoint 2.3.3, §22/§23/§28): this is
            // NOT a silent permanent no-op. CP2.2's send path commits
            // Message as Sent (with ProviderMessageId) only AFTER the Meta
            // HTTP round trip completes — Meta could already fire this exact
            // webhook before that commit lands, a genuine transient race,
            // not just a hypothetical one. Throwing (never swallowing) lets
            // Wolverine's bounded retry policy (WhatsAppMessageStatusChangedHandler.Configure,
            // Checkpoint 2.3.3.1) handle both cases: the race self-heals on
            // a later retry once our own commit has landed; a genuinely
            // orphaned ProviderMessageId exhausts the bounded retries and
            // reaches Wolverine's own terminal error handling instead of
            // silently vanishing.
            //
            // Deliberately WhatsAppMessageNotYetAvailableException, never a
            // generic InvalidOperationException (Checkpoint 2.3.3.1 second
            // correction): that policy is scoped to exactly this exception
            // type — an unrelated bug elsewhere in this method throwing a
            // generic InvalidOperationException must never accidentally
            // receive the same retry treatment.
            _logger.LogWarning(
                "{AuditEvent}: tenant {TenantId} providerMessageId {ProviderMessageId} incomingStatus {IncomingStatus} " +
                "occurredAtUtc {OccurredAtUtc} correlation {CorrelationId} durationMs {DurationMs}",
                UnknownMessage, @event.TenantId, @event.ProviderMessageId, @event.Status,
                @event.OccurredAtUtc, @event.CorrelationId, stopwatch.ElapsedMilliseconds);

            throw new WhatsAppMessageNotYetAvailableException(
                $"No Message found for ProviderMessageId in tenant {@event.TenantId} (event {@event.EventId}) — retrying.");
        }

        var previousStatus = message.Status;
        var result = message.ApplyProviderStatus(MapStatus(@event.Status), @event.OccurredAtUtc, @event.ProviderErrorCode);

        if (result == ProviderStatusApplicationResult.Applied)
            await UpdateAsync(message, cancellationToken);

        LogResult(@event, message, previousStatus, result, stopwatch.ElapsedMilliseconds);
    }

    private Task UpdateAsync(Message message, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            _repository.Update(message);
            return Task.FromResult(true);
        }, cancellationToken);

    private void LogResult(
        WhatsAppMessageStatusChanged @event, Message message, MessageStatus previousStatus,
        ProviderStatusApplicationResult result, long durationMs)
    {
        var auditEvent = result switch
        {
            ProviderStatusApplicationResult.Applied when message.Status == MessageStatus.Failed => AppliedFailed,
            ProviderStatusApplicationResult.Applied => Applied,
            ProviderStatusApplicationResult.Duplicate => Duplicate,
            ProviderStatusApplicationResult.Regression => Regression,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
        };

        _logger.LogInformation(
            "{AuditEvent}: tenant {TenantId} messageId {MessageId} providerMessageId {ProviderMessageId} " +
            "incomingStatus {IncomingStatus} previousStatus {PreviousStatus} result {Result} " +
            "hasErrorCode {HasErrorCode} occurredAtUtc {OccurredAtUtc} correlation {CorrelationId} durationMs {DurationMs}",
            auditEvent, @event.TenantId, message.Id, @event.ProviderMessageId, @event.Status, previousStatus, result,
            @event.ProviderErrorCode is not null, @event.OccurredAtUtc, @event.CorrelationId, durationMs);
    }

    private static WhatsAppProviderStatus MapStatus(WhatsAppMessageProviderStatus status) => status switch
    {
        WhatsAppMessageProviderStatus.Sent => WhatsAppProviderStatus.Sent,
        WhatsAppMessageProviderStatus.Delivered => WhatsAppProviderStatus.Delivered,
        WhatsAppMessageProviderStatus.Read => WhatsAppProviderStatus.Read,
        WhatsAppMessageProviderStatus.Failed => WhatsAppProviderStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unrecognized WhatsAppMessageProviderStatus — ExternalIntegrations should never publish an unmapped status."),
    };
}
