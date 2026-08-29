using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Front Desk ("Portaria") operational notification for an automatically
/// approved late checkout (Fase 10, Checkpoint 4). Mirrors
/// <see cref="GuestCheckedInFrontDeskNotificationProcessor"/>'s structure
/// exactly. This is the THIRD independent consumer of
/// <see cref="LateCheckoutApproved"/> (alongside Workflow Orchestration's
/// reschedule orchestrator and Housekeeping's cleaning reactor, ADR-020) —
/// never gated on <see cref="LateCheckoutApproved.UpdatesCleaning"/>, which
/// is Housekeeping's own gate, unrelated to whether the front desk should be
/// notified. Never published for the <c>PendingPayment</c> outcome (this
/// event simply is not raised then) — no special-casing needed here.
/// </summary>
public sealed class LateCheckoutApprovedFrontDeskNotificationProcessor : IIntegrationEventHandler<LateCheckoutApproved>
{
    private const string Channel = "WhatsApp";
    private const string TemplateKey = "FRONT_DESK_LATE_CHECKOUT_APPROVED";
    private const string GuestNameVariable = "GuestName";
    private const string ApprovedCheckOutAtVariable = "ApprovedCheckOutAt";
    private const string FrontDeskContactNotConfiguredReason = "FrontDeskContactNotConfigured";
    private const string NoActiveTemplateReason = "NoActiveTemplate";
    private const string ConnectorExceptionFailureReason = "connector_exception";
    private const string ConnectorRejectedFailureReasonDefault = "connector_rejected";

    private readonly IFrontDeskContactReader _frontDeskContactReader;
    private readonly ITemplateReader _templateReader;
    private readonly IReservationGuestContactReader _guestContactReader;
    private readonly IMessageRepository _repository;
    private readonly IConversationResolver _conversationResolver;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly IOutboundMessageConnector _connector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LateCheckoutApprovedFrontDeskNotificationProcessor> _logger;

    public LateCheckoutApprovedFrontDeskNotificationProcessor(
        IFrontDeskContactReader frontDeskContactReader,
        ITemplateReader templateReader,
        IReservationGuestContactReader guestContactReader,
        IMessageRepository repository,
        IConversationResolver conversationResolver,
        ICommunicationTransactionExecutor transactionExecutor,
        IOutboundMessageConnector connector,
        TimeProvider timeProvider,
        ILogger<LateCheckoutApprovedFrontDeskNotificationProcessor> logger)
    {
        _frontDeskContactReader = frontDeskContactReader;
        _templateReader = templateReader;
        _guestContactReader = guestContactReader;
        _repository = repository;
        _conversationResolver = conversationResolver;
        _transactionExecutor = transactionExecutor;
        _connector = connector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(LateCheckoutApproved @event, CancellationToken cancellationToken)
    {
        var idempotencyKey = BuildIdempotencyKey(@event.TenantId, @event.ReservationId, TemplateKey, Channel);

        var existing = await _transactionExecutor.ExecuteAsync(
            () => _repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken), cancellationToken);
        if (existing is not null)
        {
            LogSkipped(@event, "AlreadyQueuedOrSent");
            return;
        }

        var frontDeskContact = await _frontDeskContactReader.GetActiveByPropertyIdAsync(
            @event.TenantId, @event.PropertyId, cancellationToken);
        if (frontDeskContact is null)
        {
            LogSkipped(@event, FrontDeskContactNotConfiguredReason);
            return;
        }

        var template = await _templateReader.GetActiveByKeyAsync(@event.TenantId, TemplateKey, cancellationToken);
        if (template is null)
        {
            LogSkipped(@event, NoActiveTemplateReason);
            return;
        }

        var guestContact = await _guestContactReader.GetGuestContactAsync(@event.TenantId, @event.ReservationId, cancellationToken);

        var templateVariables = new Dictionary<string, string>
        {
            [GuestNameVariable] = guestContact?.GuestName ?? string.Empty,
            [ApprovedCheckOutAtVariable] = @event.ApprovedCheckOutAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm"),
        };
        var renderedContent = TemplateRenderer.Render(template.Content, templateVariables);

        var now = _timeProvider.GetUtcNow();
        var conversationId = await _conversationResolver.GetOrCreateActiveConversationIdAsync(
            @event.TenantId, @event.ReservationId, Channel, now, cancellationToken);
        var message = Message.Create(
            Guid.NewGuid(), @event.TenantId, conversationId, @event.ReservationId, Channel, TemplateKey,
            Mask(frontDeskContact.PhoneNumber), renderedContent, idempotencyKey, now);
        message.MarkQueued();

        await InsertAsync(message, cancellationToken);
        LogResult(@event, message);

        message.MarkSending();

        OutboundMessageDispatchResult dispatchResult;
        try
        {
            dispatchResult = await _connector.SendAsync(
                new OutboundMessageDispatch(
                    @event.TenantId, message.Id, frontDeskContact.PhoneNumber, TemplateKey, templateVariables, renderedContent, idempotencyKey),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Communication front desk connector threw for tenant {TenantId} reservationId {ReservationId} messageId {MessageId}",
                @event.TenantId, @event.ReservationId, message.Id);

            message.MarkFailed(ConnectorExceptionFailureReason, _timeProvider.GetUtcNow());
            await UpdateAsync(message, cancellationToken);
            throw;
        }

        if (dispatchResult.Success)
            message.MarkSent(_timeProvider.GetUtcNow(), dispatchResult.ProviderMessageId);
        else
            message.MarkFailed(dispatchResult.FailureReason ?? ConnectorRejectedFailureReasonDefault, _timeProvider.GetUtcNow());

        await UpdateAsync(message, cancellationToken);
        LogResult(@event, message);
    }

    private Task InsertAsync(Message message, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            _repository.Add(message);
            return Task.FromResult(true);
        }, cancellationToken);

    private Task UpdateAsync(Message message, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            _repository.Update(message);
            return Task.FromResult(true);
        }, cancellationToken);

    private void LogSkipped(LateCheckoutApproved @event, string reasonCode) =>
        _logger.LogInformation(
            "Communication {Trigger} skipped for tenant {TenantId} reservationId {ReservationId} propertyId {PropertyId}: {Result} (reasonCode {ReasonCode})",
            nameof(LateCheckoutApproved), @event.TenantId, @event.ReservationId, @event.PropertyId, "FrontDeskNotificationSkipped", reasonCode);

    private void LogResult(LateCheckoutApproved @event, Message message) =>
        _logger.LogInformation(
            "Communication {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservationId {ReservationId} " +
            "(source event {SourceEventId}, correlation {CorrelationId}): {Channel}/{TemplateKey} messageId {MessageId} — result {Result}",
            "SendFrontDeskLateCheckoutApprovedNotification", nameof(LateCheckoutApproved), "System", @event.TenantId, @event.ReservationId,
            @event.EventId, @event.CorrelationId, Channel, TemplateKey, message.Id, message.Status);

    private static string BuildIdempotencyKey(Guid tenantId, Guid reservationId, string templateKey, string channel) =>
        $"{tenantId:D}:{reservationId:D}:{templateKey}:{channel}";

    /// <summary>Never persists/logs the front desk contact's full phone — only the last four characters, mirrors <see cref="ReservationCreatedCommunicationProcessor"/>'s own masking algorithm.</summary>
    private static string Mask(string phone) =>
        phone.Length <= 4
            ? new string('*', phone.Length)
            : new string('*', phone.Length - 4) + phone[^4..];
}
