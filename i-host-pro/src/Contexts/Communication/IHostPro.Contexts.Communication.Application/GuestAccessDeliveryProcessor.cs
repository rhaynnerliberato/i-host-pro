using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Delivers a guest's access credential and/or access instructions (Fase
/// 10, Checkpoint 6.2 — Guest Access Secure Delivery Corrective
/// Implementation). Reacts to <see cref="GuestAccessDeliveryRequested"/>,
/// which carries neither the credential nor the instructions content —
/// this processor resolves them separately, synchronously, through
/// <see cref="IPropertyGuestAccessReader"/> (ADR-028, exception #12) at the
/// exact moment it is about to render and send.
///
/// Two independent business intents, one event (CP6.1 Decision Gate item
/// 23): <see cref="DeliverCredentialAsync"/> (sensitive — see below) and
/// <see cref="DeliverInstructionsAsync"/> (ordinary, non-secret content,
/// uses the standard Communication pipeline exactly like
/// <see cref="PixChargeCreatedDeliveryProcessor"/>). Each has its own
/// idempotency key (<see cref="TemplateKey"/> differs), so a missing
/// credential never blocks instructions delivery and vice versa. Wolverine
/// resolves exactly one <see cref="IIntegrationEventHandler{TEvent}"/> per
/// event type through <c>CommunicationMessageExecutionScope</c>'s keyed
/// single-service resolution — two internal delivery paths inside ONE
/// handler is how this platform expresses "one event, two side effects"
/// without introducing a second competing registration.
///
/// CRITICAL security property (CP6.1 Decision Gate item 16, CP6.2 mandate
/// items 15-17): the resolved credential is <b>never</b> passed as the
/// <c>renderedContent</c> argument to <see cref="Message.Create"/> — doing
/// so would persist it in plaintext in <c>communication.messages.rendered_content</c>
/// forever (confirmed: that column has no encryption/redaction of its own).
/// The REAL rendered content (containing the credential) is built only in
/// memory and passed ONLY to <see cref="IOutboundMessageConnector.SendAsync"/>
/// — its own intended final destination, mirrors how the PIX QR payload is
/// legitimately rendered into a message body (ADR-025). The value persisted
/// via <see cref="Message.Create"/> for the credential intent is always the
/// fixed <see cref="RedactedContentMarker"/> — never the real content, never
/// a partially-redacted derivative of it.
/// </summary>
public sealed class GuestAccessDeliveryProcessor : IIntegrationEventHandler<GuestAccessDeliveryRequested>
{
    private const string Channel = "WhatsApp";
    private const string CredentialTemplateKey = "GUEST_ACCESS_CREDENTIAL";
    private const string InstructionsTemplateKey = "GUEST_ACCESS_INSTRUCTIONS";
    private const string GuestNameVariable = "GuestName";
    private const string AccessCredentialVariable = "AccessCredential";
    private const string AccessInstructionsVariable = "AccessInstructions";
    private const string RedactedContentMarker = "[SENSITIVE CONTENT REDACTED]";
    private const string NoActiveConfigurationReason = "NoActivePropertyAccessConfiguration";
    private const string NoActiveTemplateReason = "NoActiveTemplate";
    private const string NoCredentialConfiguredReason = "NoAccessCredentialConfigured";
    private const string NoInstructionsConfiguredReason = "NoAccessInstructionsConfigured";
    private const string GuestContactOrPhoneNotAvailableReason = "GuestContactOrPhoneNotAvailable";
    private const string ConnectorExceptionFailureReason = "connector_exception";
    private const string ConnectorRejectedFailureReasonDefault = "connector_rejected";

    private readonly IPropertyGuestAccessReader _propertyGuestAccessReader;
    private readonly ITemplateReader _templateReader;
    private readonly IReservationGuestContactReader _guestContactReader;
    private readonly IMessageRepository _repository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly IOutboundMessageConnector _connector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GuestAccessDeliveryProcessor> _logger;

    public GuestAccessDeliveryProcessor(
        IPropertyGuestAccessReader propertyGuestAccessReader,
        ITemplateReader templateReader,
        IReservationGuestContactReader guestContactReader,
        IMessageRepository repository,
        ICommunicationTransactionExecutor transactionExecutor,
        IOutboundMessageConnector connector,
        TimeProvider timeProvider,
        ILogger<GuestAccessDeliveryProcessor> logger)
    {
        _propertyGuestAccessReader = propertyGuestAccessReader;
        _templateReader = templateReader;
        _guestContactReader = guestContactReader;
        _repository = repository;
        _transactionExecutor = transactionExecutor;
        _connector = connector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(GuestAccessDeliveryRequested @event, CancellationToken cancellationToken)
    {
        var access = await _propertyGuestAccessReader.GetForGuestAccessDeliveryAsync(@event.TenantId, @event.PropertyId, cancellationToken);
        if (access is null)
        {
            LogSkipped(@event, CredentialTemplateKey, NoActiveConfigurationReason);
            LogSkipped(@event, InstructionsTemplateKey, NoActiveConfigurationReason);
            return;
        }

        var guestContact = await _guestContactReader.GetGuestContactAsync(@event.TenantId, @event.ReservationId, cancellationToken);
        if (guestContact?.GuestPhone is null)
        {
            LogSkipped(@event, CredentialTemplateKey, GuestContactOrPhoneNotAvailableReason);
            LogSkipped(@event, InstructionsTemplateKey, GuestContactOrPhoneNotAvailableReason);
            return;
        }

        if (access.AccessCredential is null)
            LogSkipped(@event, CredentialTemplateKey, NoCredentialConfiguredReason);
        else
            await DeliverCredentialAsync(@event, guestContact.GuestName, guestContact.GuestPhone, access.AccessCredential, cancellationToken);

        if (access.AccessInstructions is null)
            LogSkipped(@event, InstructionsTemplateKey, NoInstructionsConfiguredReason);
        else
            await DeliverInstructionsAsync(@event, guestContact.GuestName, guestContact.GuestPhone, access.AccessInstructions, cancellationToken);
    }

    /// <summary>
    /// Sensitive transient delivery: the real credential is rendered and
    /// sent to <see cref="IOutboundMessageConnector"/>, but the persisted
    /// <see cref="Message"/> NEVER carries it — see this class's own doc
    /// comment for the full security rationale.
    /// </summary>
    private async Task DeliverCredentialAsync(
        GuestAccessDeliveryRequested @event, string guestName, string guestPhone, string accessCredential, CancellationToken cancellationToken)
    {
        var idempotencyKey = BuildIdempotencyKey(@event.TenantId, @event.AggregateId, CredentialTemplateKey, Channel);

        var existing = await _transactionExecutor.ExecuteAsync(
            () => _repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken), cancellationToken);
        if (existing is not null)
        {
            LogSkipped(@event, CredentialTemplateKey, "AlreadyQueuedOrSent");
            return;
        }

        var template = await _templateReader.GetActiveByKeyAsync(@event.TenantId, CredentialTemplateKey, cancellationToken);
        if (template is null)
        {
            LogSkipped(@event, CredentialTemplateKey, NoActiveTemplateReason);
            return;
        }

        var templateVariables = new Dictionary<string, string>
        {
            [GuestNameVariable] = guestName,
            [AccessCredentialVariable] = accessCredential,
        };
        // The ONLY place the real credential is ever rendered — kept in a
        // local variable, never assigned to anything this method persists.
        var sensitiveRenderedContent = TemplateRenderer.Render(template.Content, templateVariables);

        var now = _timeProvider.GetUtcNow();
        var message = Message.Create(
            Guid.NewGuid(), @event.TenantId, @event.ReservationId, Channel, CredentialTemplateKey,
            Mask(guestPhone), RedactedContentMarker, idempotencyKey, now);
        message.MarkQueued();

        await InsertAsync(message, cancellationToken);
        LogResult(@event, message);

        message.MarkSending();

        OutboundMessageDispatchResult dispatchResult;
        try
        {
            dispatchResult = await _connector.SendAsync(
                new OutboundMessageDispatch(
                    @event.TenantId, message.Id, guestPhone, CredentialTemplateKey, templateVariables, sensitiveRenderedContent, idempotencyKey),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Communication guest access credential delivery connector threw for tenant {TenantId} reservationId {ReservationId} messageId {MessageId}",
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

    /// <summary>Ordinary delivery — <see cref="PropertyGuestAccessReadResult.AccessInstructions"/> is not a secret, uses the standard pipeline, persisted as-is (mirrors <see cref="PixChargeCreatedDeliveryProcessor"/> exactly).</summary>
    private async Task DeliverInstructionsAsync(
        GuestAccessDeliveryRequested @event, string guestName, string guestPhone, string accessInstructions, CancellationToken cancellationToken)
    {
        var idempotencyKey = BuildIdempotencyKey(@event.TenantId, @event.AggregateId, InstructionsTemplateKey, Channel);

        var existing = await _transactionExecutor.ExecuteAsync(
            () => _repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken), cancellationToken);
        if (existing is not null)
        {
            LogSkipped(@event, InstructionsTemplateKey, "AlreadyQueuedOrSent");
            return;
        }

        var template = await _templateReader.GetActiveByKeyAsync(@event.TenantId, InstructionsTemplateKey, cancellationToken);
        if (template is null)
        {
            LogSkipped(@event, InstructionsTemplateKey, NoActiveTemplateReason);
            return;
        }

        var templateVariables = new Dictionary<string, string>
        {
            [GuestNameVariable] = guestName,
            [AccessInstructionsVariable] = accessInstructions,
        };
        var renderedContent = TemplateRenderer.Render(template.Content, templateVariables);

        var now = _timeProvider.GetUtcNow();
        var message = Message.Create(
            Guid.NewGuid(), @event.TenantId, @event.ReservationId, Channel, InstructionsTemplateKey,
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
                    @event.TenantId, message.Id, guestPhone, InstructionsTemplateKey, templateVariables, renderedContent, idempotencyKey),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Communication guest access instructions delivery connector threw for tenant {TenantId} reservationId {ReservationId} messageId {MessageId}",
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

    private void LogSkipped(GuestAccessDeliveryRequested @event, string templateKey, string reasonCode) =>
        _logger.LogInformation(
            "Communication {Trigger} skipped for tenant {TenantId} reservationId {ReservationId} propertyId {PropertyId} templateKey {TemplateKey}: {Result} (reasonCode {ReasonCode})",
            nameof(GuestAccessDeliveryRequested), @event.TenantId, @event.ReservationId, @event.PropertyId, templateKey, "GuestAccessDeliverySkipped", reasonCode);

    private void LogResult(GuestAccessDeliveryRequested @event, Message message) =>
        _logger.LogInformation(
            "Communication {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservationId {ReservationId} " +
            "(source event {SourceEventId}, correlation {CorrelationId}): {Channel}/{TemplateKey} messageId {MessageId} — result {Result}",
            "SendGuestAccessDelivery", nameof(GuestAccessDeliveryRequested), "System", @event.TenantId, @event.ReservationId,
            @event.EventId, @event.CorrelationId, Channel, message.TemplateKey, message.Id, message.Status);

    private static string BuildIdempotencyKey(Guid tenantId, Guid guestStayOperationId, string templateKey, string channel) =>
        $"{tenantId:D}:{guestStayOperationId:D}:{templateKey}:{channel}";

    /// <summary>Never persists/logs the guest's full phone — only the last four characters, mirrors every other Communication processor's own masking algorithm.</summary>
    private static string Mask(string phone) =>
        phone.Length <= 4
            ? new string('*', phone.Length)
            : new string('*', phone.Length - 4) + phone[^4..];
}
