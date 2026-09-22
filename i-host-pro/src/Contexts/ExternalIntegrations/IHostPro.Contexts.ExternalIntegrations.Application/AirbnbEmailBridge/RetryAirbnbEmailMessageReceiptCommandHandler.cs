using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Reconstructs and republishes one terminal receipt via Graph refetch +
/// reparse (Airbnb Email Operational Exception Resolution gate) — never by
/// persisting a second reservation snapshot (Section 9 of the approved
/// design: refetch is always preferred over persisting PII).
///
/// Deliberately NOT wrapped by the generic <c>TenantTransactionBehavior</c>
/// (no such registration exists for this command — see
/// <c>ExternalIntegrationsCommandDispatchExtensions</c>): this handler calls
/// <see cref="IExternalIntegrationsTransactionExecutor"/> (directly for the
/// read-only precondition check, and via <see cref="IRetryAirbnbEmailMessageReceiptExecutor"/>
/// for the mutating/publishing transaction, which also translates a
/// concurrent-retry <c>DbUpdateConcurrencyException</c> into
/// <see cref="AirbnbEmailBridgeErrorCodes.ReceiptRetryConflict"/> without
/// this Application-layer class ever referencing EF Core) — exactly like
/// <see cref="AirbnbEmailDeltaSyncRunner"/> does, for the same reason: the
/// publish path below must enqueue its event into the SAME transaction that
/// mutates the receipt, and wrapping this command in BOTH an ambient
/// behavior and its own executor would nest a second transaction on the same
/// <c>ExternalIntegrationsDbContext</c> instance (the exact defect
/// <c>AirbnbAutoPublishNestedTransactionRegressionTests</c> proved and fixed
/// for the delta-sync path).
///
/// Structure mirrors <see cref="AirbnbEmailDeltaSyncRunner.RunAsync"/>
/// exactly: short transactions bookend the external Graph HTTP call, which
/// must never hold a database transaction open. The outcome-application
/// branching in <see cref="ApplyOutcomeAsync"/> is intentionally a close
/// mirror of <c>AirbnbEmailDeltaSyncRunner.ApplyDryRunOutcomeAsync</c> (not a
/// shared abstraction — kept small and duplicated deliberately, since
/// extracting it would touch the just-fixed, already-proven delta-sync
/// runner for a benefit no bigger than avoiding ~30 duplicated lines); any
/// future change to one MUST be mirrored in the other.
/// </summary>
public sealed class RetryAirbnbEmailMessageReceiptCommandHandler
    : ICommandHandler<RetryAirbnbEmailMessageReceiptCommand, AirbnbEmailMessageReceiptResult>
{
    private const string ReservationParserVersion = "airbnb-reservation-reminder-v1";
    private const string ReservationDetectedEventType = "RESERVATION_REMINDER";
    private const string PublisherFailureReason = "PublisherFailure";

    private static readonly Error NotFoundError = new(AirbnbEmailBridgeErrorCodes.ReceiptNotFound, AirbnbEmailBridgeErrorCodes.ReceiptNotFound);
    private static readonly Error NotRetryableError = new(AirbnbEmailBridgeErrorCodes.ReceiptNotRetryable, AirbnbEmailBridgeErrorCodes.ReceiptNotRetryable);
    private static readonly Error MailboxNotConnectedError = new(AirbnbEmailBridgeErrorCodes.ReceiptMailboxNotConnected, AirbnbEmailBridgeErrorCodes.ReceiptMailboxNotConnected);
    private static readonly Error SourceMessageUnavailableError = new(AirbnbEmailBridgeErrorCodes.ReceiptSourceMessageUnavailable, AirbnbEmailBridgeErrorCodes.ReceiptSourceMessageUnavailable);

    private readonly IAirbnbEmailMessageReceiptRepository _receiptRepository;
    private readonly IAirbnbEmailMailboxConnectionRepository _connectionRepository;
    private readonly IAirbnbEmailAuthenticator _authenticator;
    private readonly IAirbnbEmailMessageSource _messageSource;
    private readonly IAirbnbReservationDryRunEvaluator _dryRunEvaluator;
    private readonly IAirbnbResolvedReservationSyncPublisher _resolvedPublisher;
    private readonly IExternalIntegrationsTransactionExecutor _transactionExecutor;
    private readonly IRetryAirbnbEmailMessageReceiptExecutor _retryExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetryAirbnbEmailMessageReceiptCommandHandler> _logger;

    public RetryAirbnbEmailMessageReceiptCommandHandler(
        IAirbnbEmailMessageReceiptRepository receiptRepository,
        IAirbnbEmailMailboxConnectionRepository connectionRepository,
        IAirbnbEmailAuthenticator authenticator,
        IAirbnbEmailMessageSource messageSource,
        IAirbnbReservationDryRunEvaluator dryRunEvaluator,
        IAirbnbResolvedReservationSyncPublisher resolvedPublisher,
        IExternalIntegrationsTransactionExecutor transactionExecutor,
        IRetryAirbnbEmailMessageReceiptExecutor retryExecutor,
        TimeProvider timeProvider,
        ILogger<RetryAirbnbEmailMessageReceiptCommandHandler> logger)
    {
        _receiptRepository = receiptRepository;
        _connectionRepository = connectionRepository;
        _authenticator = authenticator;
        _messageSource = messageSource;
        _dryRunEvaluator = dryRunEvaluator;
        _resolvedPublisher = resolvedPublisher;
        _transactionExecutor = transactionExecutor;
        _retryExecutor = retryExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<AirbnbEmailMessageReceiptResult>> Handle(
        RetryAirbnbEmailMessageReceiptCommand command, CancellationToken cancellationToken)
    {
        var precheck = await _transactionExecutor.ExecuteAsync(async () =>
        {
            var receipt = await _receiptRepository.GetByIdAsync(command.ReceiptId, cancellationToken);
            if (receipt is null || receipt.TenantId != command.TenantId)
                return (Found: false, Retryable: false, GraphMessageId: (string?)null, ReceivedAtUtc: default(DateTimeOffset));

            return (Found: true, Retryable: IsRetryable(receipt), receipt.GraphMessageId, receipt.ReceivedAtUtc);
        }, cancellationToken);

        if (!precheck.Found)
            return Result.Failure<AirbnbEmailMessageReceiptResult>(NotFoundError);
        if (!precheck.Retryable)
            return Result.Failure<AirbnbEmailMessageReceiptResult>(NotRetryableError);

        // Silent auth and the Graph fetch are both external calls - never
        // performed while holding a database transaction open (mirrors
        // AirbnbEmailDeltaSyncRunner.RunAsync exactly).
        var authOutcome = await _authenticator.AcquireTokenSilentAsync(command.TenantId, cancellationToken);
        if (!authOutcome.IsSuccess)
            return Result.Failure<AirbnbEmailMessageReceiptResult>(MailboxNotConnectedError);

        var content = await _messageSource.GetMessageContentAsync(authOutcome.AccessToken!, precheck.GraphMessageId!, cancellationToken);
        if (content?.Body is null)
            return Result.Failure<AirbnbEmailMessageReceiptResult>(SourceMessageUnavailableError);

        // Reparse using the ORIGINAL receipt timestamp for the historical
        // cutoff decision below - never "now" (Section 10 of the approved
        // design: a receipt that predates AutoPublishNotBeforeUtc must never
        // become publishable only because an admin retried it later).
        var outcome = await _dryRunEvaluator.EvaluateAsync(
            content.Subject ?? string.Empty, content.Body, precheck.ReceivedAtUtc, cancellationToken);

        return await _retryExecutor.ExecuteAsync(async () =>
        {
            // Reloaded fresh, inside this transaction, so the receipt's xmin
            // reflects the current row - a second concurrent retry (or the
            // delta-sync worker) that changed it since the precheck above is
            // caught here, and again by the database's own concurrency check
            // when this transaction commits (translated by
            // IRetryAirbnbEmailMessageReceiptExecutor into ReceiptRetryConflict).
            var receipt = await _receiptRepository.GetByIdAsync(command.ReceiptId, cancellationToken);
            if (receipt is null || receipt.TenantId != command.TenantId || !IsRetryable(receipt))
                return Result.Failure<AirbnbEmailMessageReceiptResult>(NotRetryableError);

            var connection = await _connectionRepository.GetForCurrentTenantAsync(cancellationToken);
            var processedAtUtc = _timeProvider.GetUtcNow();

            await ApplyOutcomeAsync(connection, precheck.ReceivedAtUtc, command.TenantId, receipt, outcome, processedAtUtc, cancellationToken);

            return Result.Success(new AirbnbEmailMessageReceiptResult(
                receipt.Id, receipt.ReceivedAtUtc, receipt.ProcessingStatus.ToString(), receipt.DetectedEventType,
                receipt.ParserVersion, receipt.FailureReason, receipt.UnmatchedListingTitle, receipt.ProcessedAtUtc, receipt.CreatedAtUtc));
        }, cancellationToken);
    }

    /// <summary>Approved candidate retry scope (Section 12): NeedsReview, or Failed with reason PublisherFailure. Nothing else.</summary>
    private static bool IsRetryable(AirbnbEmailMessageReceipt receipt) =>
        receipt.ProcessingStatus == AirbnbEmailMessageProcessingStatus.NeedsReview
        || (receipt.ProcessingStatus == AirbnbEmailMessageProcessingStatus.Failed && receipt.FailureReason == PublisherFailureReason);

    /// <summary>Intentional close mirror of <c>AirbnbEmailDeltaSyncRunner.ApplyDryRunOutcomeAsync</c> — see this class's own doc comment.</summary>
    private async Task ApplyOutcomeAsync(
        AirbnbEmailMailboxConnection? connection, DateTimeOffset messageReceivedAtUtc, Guid tenantId,
        AirbnbEmailMessageReceipt receipt, AirbnbReservationDryRunOutcome outcome, DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken)
    {
        if (outcome.WouldImport)
        {
            if (connection is null || !connection.AutoPublishEnabled)
            {
                receipt.MarkProcessed(ReservationDetectedEventType, outcome.ExternalReservationId, ReservationParserVersion, processedAtUtc);
            }
            else if (connection.AutoPublishNotBeforeUtc is { } notBeforeUtc && messageReceivedAtUtc < notBeforeUtc)
            {
                receipt.MarkIgnored(ReservationDetectedEventType, ReservationParserVersion, processedAtUtc);
            }
            else
            {
                try
                {
                    await _resolvedPublisher.PublishReservationImportedAsync(
                        outcome.ResolvedPropertyId!.Value, outcome.ExternalReservationId!, outcome.GuestName!,
                        outcome.CheckInAt!.Value, outcome.CheckOutAt!.Value, outcome.GuestCount!.Value,
                        occurredAtUtc: processedAtUtc, correlationId: Guid.NewGuid(), cancellationToken);
                    receipt.MarkProcessed(ReservationDetectedEventType, outcome.ExternalReservationId, ReservationParserVersion, processedAtUtc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Airbnb resolved-property publish failed while retrying a receipt for tenant {TenantId} - the receipt remains Failed/PublisherFailure for a later retry.",
                        tenantId);
                    receipt.MarkFailed(PublisherFailureReason, ReservationParserVersion, processedAtUtc);
                }
            }
        }
        else if (outcome.ParseFailureReason is { } failureReason)
        {
            // Genuinely unexpected on a retry (the same message parsed
            // successfully before) - handled the same way the normal flow
            // would, never rethrown, since a re-parse failure is still a
            // legitimate (if surprising) outcome to record on the receipt.
            receipt.MarkFailed(failureReason.ToString(), ReservationParserVersion, processedAtUtc);
        }
        else
        {
            receipt.MarkNeedsReview(ReservationDetectedEventType, ReservationParserVersion, processedAtUtc, outcome.UnmatchedListingTitle);
        }
    }
}
