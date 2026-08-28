using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Delivers a PIX charge's QR/copy-paste payload to the guest (Fase 10,
/// Checkpoint 5 — PIX/Payment Deterministic Foundation). Mirrors
/// <see cref="LateCheckoutApprovedFrontDeskNotificationProcessor"/>'s
/// structure exactly, with one deliberate difference: the recipient is the
/// GUEST (via <see cref="IReservationGuestContactReader"/>, ADR-019), not
/// the front desk.
///
/// <see cref="PixChargeCreated"/> deliberately carries no QR/financial
/// payload (ADR-025/ADR-027) — this processor resolves it separately,
/// synchronously, through <see cref="IPixChargeDeliveryReader"/> (ADR-027,
/// exception #11) at the exact moment it is about to render and send the
/// guest message. The QR/copy-paste payload is rendered into the outbound
/// message CONTENT itself — that is its intended final destination (the
/// guest is meant to read/scan it); the "never log/never in event/never in
/// query string" rules protect every OTHER internal boundary, not this one.
/// </summary>
public sealed class PixChargeCreatedDeliveryProcessor : IIntegrationEventHandler<PixChargeCreated>
{
    private const string Channel = "WhatsApp";
    private const string TemplateKey = "LATE_CHECKOUT_PIX_PAYMENT";
    private const string GuestNameVariable = "GuestName";
    private const string AmountVariable = "Amount";
    private const string PixCodeVariable = "PixCode";
    private const string PixChargeNotFoundReason = "PixChargeNotFoundForDelivery";
    private const string NoActiveTemplateReason = "NoActiveTemplate";
    private const string ConnectorExceptionFailureReason = "connector_exception";
    private const string ConnectorRejectedFailureReasonDefault = "connector_rejected";

    private readonly IPixChargeDeliveryReader _pixChargeDeliveryReader;
    private readonly ITemplateReader _templateReader;
    private readonly IReservationGuestContactReader _guestContactReader;
    private readonly IMessageRepository _repository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly IOutboundMessageConnector _connector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PixChargeCreatedDeliveryProcessor> _logger;

    public PixChargeCreatedDeliveryProcessor(
        IPixChargeDeliveryReader pixChargeDeliveryReader,
        ITemplateReader templateReader,
        IReservationGuestContactReader guestContactReader,
        IMessageRepository repository,
        ICommunicationTransactionExecutor transactionExecutor,
        IOutboundMessageConnector connector,
        TimeProvider timeProvider,
        ILogger<PixChargeCreatedDeliveryProcessor> logger)
    {
        _pixChargeDeliveryReader = pixChargeDeliveryReader;
        _templateReader = templateReader;
        _guestContactReader = guestContactReader;
        _repository = repository;
        _transactionExecutor = transactionExecutor;
        _connector = connector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(PixChargeCreated @event, CancellationToken cancellationToken)
    {
        var idempotencyKey = BuildIdempotencyKey(@event.TenantId, @event.AggregateId, TemplateKey, Channel);

        var existing = await _transactionExecutor.ExecuteAsync(
            () => _repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken), cancellationToken);
        if (existing is not null)
        {
            LogSkipped(@event, "AlreadyQueuedOrSent");
            return;
        }

        var delivery = await _pixChargeDeliveryReader.GetForDeliveryAsync(@event.TenantId, @event.AggregateId, cancellationToken);
        if (delivery is null)
        {
            LogSkipped(@event, PixChargeNotFoundReason);
            return;
        }

        var template = await _templateReader.GetActiveByKeyAsync(@event.TenantId, TemplateKey, cancellationToken);
        if (template is null)
        {
            LogSkipped(@event, NoActiveTemplateReason);
            return;
        }

        var guestContact = await _guestContactReader.GetGuestContactAsync(@event.TenantId, @event.ReservationId, cancellationToken);
        if (guestContact?.GuestPhone is null)
        {
            LogSkipped(@event, "GuestContactOrPhoneNotAvailable");
            return;
        }

        var templateVariables = new Dictionary<string, string>
        {
            [GuestNameVariable] = guestContact.GuestName,
            [AmountVariable] = delivery.Amount.ToString("F2"),
            [PixCodeVariable] = delivery.QrCodePayload,
        };
        var renderedContent = TemplateRenderer.Render(template.Content, templateVariables);

        var guestPhone = guestContact.GuestPhone;

        var now = _timeProvider.GetUtcNow();
        var message = Message.Create(
            Guid.NewGuid(), @event.TenantId, @event.ReservationId, Channel, TemplateKey,
            Mask(guestPhone), renderedContent, idempotencyKey, now);
        message.MarkQueued();

        await InsertAsync(message, cancellationToken);
        LogResult(@event, message);

        message.MarkSending();

        OutboundMessageDispatchResult dispatchResult;
        try
        {
            dispatchResult = await _connector.SendAsync(
                new OutboundMessageDispatch(
                    @event.TenantId, message.Id, guestPhone, TemplateKey, templateVariables, renderedContent, idempotencyKey),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Communication PIX delivery connector threw for tenant {TenantId} reservationId {ReservationId} messageId {MessageId}",
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

    private void LogSkipped(PixChargeCreated @event, string reasonCode) =>
        _logger.LogInformation(
            "Communication {Trigger} skipped for tenant {TenantId} reservationId {ReservationId} pixChargeId {PixChargeId}: {Result} (reasonCode {ReasonCode})",
            nameof(PixChargeCreated), @event.TenantId, @event.ReservationId, @event.AggregateId, "PixDeliverySkipped", reasonCode);

    private void LogResult(PixChargeCreated @event, Message message) =>
        _logger.LogInformation(
            "Communication {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservationId {ReservationId} " +
            "(source event {SourceEventId}, correlation {CorrelationId}): {Channel}/{TemplateKey} messageId {MessageId} — result {Result}",
            "SendPixChargeDeliveryToGuest", nameof(PixChargeCreated), "System", @event.TenantId, @event.ReservationId,
            @event.EventId, @event.CorrelationId, Channel, TemplateKey, message.Id, message.Status);

    private static string BuildIdempotencyKey(Guid tenantId, Guid pixChargeId, string templateKey, string channel) =>
        $"{tenantId:D}:{pixChargeId:D}:{templateKey}:{channel}";

    /// <summary>Never persists/logs the guest's full phone — only the last four characters, mirrors <see cref="ReservationCreatedCommunicationProcessor"/>'s own masking algorithm.</summary>
    private static string Mask(string phone) =>
        phone.Length <= 4
            ? new string('*', phone.Length)
            : new string('*', phone.Length - 4) + phone[^4..];
}
