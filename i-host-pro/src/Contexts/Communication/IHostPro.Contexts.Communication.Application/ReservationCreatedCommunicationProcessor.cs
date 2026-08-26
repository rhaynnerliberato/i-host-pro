using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// The sole trigger→action use case this checkpoint implements (Fase 9,
/// Checkpoint 1): reacts DIRECTLY to <see cref="ReservationCreated"/> —
/// choreography, per Fase 8's own registered criterion (single-hop,
/// stateless reaction) — never through Workflow Orchestration (CP1 mandate
/// §29). Resolves the active Template, reads the guest's contact (ADR-019),
/// renders the content, persists a <see cref="Message"/>, dispatches through
/// <see cref="IOutboundMessageConnector"/>, and records the outcome —
/// exactly the sequence the CP1 mandate (§33) describes.
///
/// Implements <see cref="IIntegrationEventHandler{TEvent}"/> directly, same
/// as every other choreography consumer in this codebase (mirrors
/// <c>ReservationCreatedCleaningOrchestrator</c>) — this class is resolved
/// exclusively from <see cref="ICommunicationMessageExecutionScope"/>'s own
/// child DI scope, never by Wolverine's own per-message resolution.
/// </summary>
public sealed class ReservationCreatedCommunicationProcessor : IIntegrationEventHandler<ReservationCreated>
{
    private const string Channel = "WhatsApp";
    private const string TemplateKey = "RESERVATION_CONFIRMATION";
    private const string CheckInDateVariable = "CheckInDate";
    private const string NoContactFailureReason = "no_contact_available";
    private const string ConnectorExceptionFailureReason = "connector_exception";
    private const string ConnectorRejectedFailureReasonDefault = "connector_rejected";

    /// <summary>
    /// <see cref="ReservationCreated.Source"/>'s stable code for an
    /// Airbnb-imported reservation (Fase 9, Checkpoint 3.2 — "Airbnb
    /// Deterministic Foundation"; CP3.1 Decision Gate item 20). Never
    /// references Reservations.Domain's <c>ReservationSource</c> enum
    /// directly — Communication may only depend on Reservations.Contracts
    /// (ADR-019), and the event already exposes this as a plain string, same
    /// convention as <see cref="Channel"/>/<see cref="TemplateKey"/> above.
    /// </summary>
    private const string AirbnbSource = "airbnb";
    private const string ConsentSkippedReasonCode = "ConsentNotEstablishedForImportedReservation";

    private readonly ITemplateReader _templateReader;
    private readonly IReservationGuestContactReader _guestContactReader;
    private readonly IMessageRepository _repository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly IOutboundMessageConnector _connector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReservationCreatedCommunicationProcessor> _logger;

    public ReservationCreatedCommunicationProcessor(
        ITemplateReader templateReader,
        IReservationGuestContactReader guestContactReader,
        IMessageRepository repository,
        ICommunicationTransactionExecutor transactionExecutor,
        IOutboundMessageConnector connector,
        TimeProvider timeProvider,
        ILogger<ReservationCreatedCommunicationProcessor> logger)
    {
        _templateReader = templateReader;
        _guestContactReader = guestContactReader;
        _repository = repository;
        _transactionExecutor = transactionExecutor;
        _connector = connector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(ReservationCreated @event, CancellationToken cancellationToken)
    {
        // Fase 9, Checkpoint 3.2 — CP3.1 Decision Gate item 20: an
        // Airbnb-imported reservation carries no established WhatsApp
        // consent/opt-in (unresolved product decision, deliberately not
        // assumed safe). Checked first, before any idempotency lookup or
        // Message creation — no Message row is ever created for this case,
        // never marked Failed (this is a deliberate skip, not a failure).
        if (@event.Source == AirbnbSource)
        {
            _logger.LogInformation(
                "Communication {Trigger} skipped for tenant {TenantId} reservation {ReservationId}: {Result} (reasonCode {ReasonCode})",
                nameof(ReservationCreated), @event.TenantId, @event.ReservationId, "ReservationCommunicationSkipped", ConsentSkippedReasonCode);
            return;
        }

        var idempotencyKey = BuildIdempotencyKey(@event.TenantId, @event.ReservationId, TemplateKey, Channel);

        var existing = await _transactionExecutor.ExecuteAsync(
            () => _repository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken), cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Communication {Trigger} skipped for tenant {TenantId} reservation {ReservationId}: {Result}",
                nameof(ReservationCreated), @event.TenantId, @event.ReservationId, "AlreadyQueuedOrSent");
            return;
        }

        var template = await _templateReader.GetActiveByKeyAsync(@event.TenantId, TemplateKey, cancellationToken);
        if (template is null)
        {
            _logger.LogInformation(
                "Communication {Trigger} skipped for tenant {TenantId} reservation {ReservationId}: {Result} (template {TemplateKey})",
                nameof(ReservationCreated), @event.TenantId, @event.ReservationId, "NoActiveTemplate", TemplateKey);
            return;
        }

        var templateVariables = BuildTemplateVariables(@event);
        var renderedContent = TemplateRenderer.Render(template.Content, templateVariables);

        var guestContact = await _guestContactReader.GetGuestContactAsync(@event.TenantId, @event.ReservationId, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var message = Message.Create(
            Guid.NewGuid(), @event.TenantId, @event.ReservationId, Channel, TemplateKey,
            Mask(guestContact?.GuestPhone), renderedContent, idempotencyKey, now);
        message.MarkQueued();

        await InsertAsync(message, cancellationToken);
        LogResult(@event, message);

        if (guestContact?.GuestPhone is null)
        {
            message.MarkFailed(NoContactFailureReason, _timeProvider.GetUtcNow());
            await UpdateAsync(message, cancellationToken);
            LogResult(@event, message);
            return;
        }

        message.MarkSending();

        OutboundMessageDispatchResult dispatchResult;
        try
        {
            dispatchResult = await _connector.SendAsync(
                new OutboundMessageDispatch(
                    @event.TenantId, message.Id, guestContact.GuestPhone, TemplateKey, templateVariables, renderedContent, idempotencyKey),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Communication connector threw for tenant {TenantId} reservation {ReservationId} messageId {MessageId}",
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

    private static Dictionary<string, string> BuildTemplateVariables(ReservationCreated @event) => new()
    {
        [CheckInDateVariable] = (@event.CheckInAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd"),
    };

    private void LogResult(ReservationCreated @event, Message message) =>
        _logger.LogInformation(
            "Communication {WorkflowName} triggered by {Trigger} as {ActorType} for tenant {TenantId} reservation {ReservationId} " +
            "(source event {SourceEventId}, correlation {CorrelationId}): {Channel}/{TemplateKey} messageId {MessageId} — result {Result}",
            "SendReservationConfirmation", nameof(ReservationCreated), "System", @event.TenantId, @event.ReservationId,
            @event.EventId, @event.CorrelationId, Channel, TemplateKey, message.Id, message.Status);

    private static string BuildIdempotencyKey(Guid tenantId, Guid reservationId, string templateKey, string channel) =>
        $"{tenantId:D}:{reservationId:D}:{templateKey}:{channel}";

    /// <summary>Never persists/logs the full phone — only the last four characters, mirrored from common carrier/WhatsApp masking conventions (see <see cref="Message.DestinationMasked"/>'s own doc comment for the design decision).</summary>
    private static string? Mask(string? phone)
    {
        if (string.IsNullOrEmpty(phone))
            return null;

        return phone.Length <= 4
            ? new string('*', phone.Length)
            : new string('*', phone.Length - 4) + phone[^4..];
    }
}
