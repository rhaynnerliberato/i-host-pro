using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Orchestrates one incremental mailbox synchronization attempt for one
/// tenant (Fase 9 review — Delta Polling gate). DRY_RUN by default: creates
/// <see cref="AirbnbEmailMessageReceipt"/> rows and, when the connection's
/// own <see cref="AirbnbEmailMailboxConnection.AutoPublishEnabled"/> is
/// false (the default for every tenant), never calls
/// <c>IAirbnbResolvedReservationSyncPublisher</c>/mutates a reservation —
/// still never touches the ExternalListingId-based
/// <c>IAirbnbReservationSyncPublisher</c> at all (Automatic Publication
/// Design + Safety gate: that publisher remains reserved for a stable-id
/// source this bridge never has).
///
/// Consistency model (Fase 9 review §9-10/§36): every database write is its
/// own committed transaction via <see cref="IExternalIntegrationsTransactionExecutor"/>,
/// re-reading the sync state fresh each time rather than threading a tracked
/// entity across calls. The delta cursor is only ever advanced
/// (<see cref="AirbnbEmailSyncState.RecordSuccess"/>) in the SAME transaction
/// that observes the final page (<c>DeltaLink</c> present) — a failure on any
/// earlier or later page leaves the cursor exactly where it was before this
/// run, and replaying from the same cursor is always safe because
/// <see cref="IAirbnbEmailMessageReceiptRepository.ExistsForCurrentTenantAsync"/>
/// makes re-observing an already-receipted message a no-op — this same
/// guard is why enabling auto-publication for a tenant can never trigger a
/// bulk replay of already-receipted historical messages (Automatic
/// Publication Design + Safety gate item 33/34): only messages the delta
/// cursor observes for the FIRST time from here on ever reach the
/// publication decision below.
///
/// AIRBNB AUTO-PUBLISH REAL TRANSACTION REGRESSION PROOF (emergency gate):
/// this runner uses <see cref="IExternalIntegrationsTransactionExecutor"/>
/// — not <see cref="IAirbnbEmailUnitOfWork"/> — precisely because the
/// receipt-processing block below may invoke
/// <see cref="IAirbnbResolvedReservationSyncPublisher.PublishReservationImportedAsync"/>,
/// which must enqueue its <c>AirbnbReservationImported</c> event into the
/// SAME already-open transaction so the receipt mutation, sync-state
/// mutation and outbox envelope commit or roll back together atomically.
/// Using <see cref="IAirbnbEmailUnitOfWork"/> here previously caused the
/// publisher's own transaction attempt to nest inside this one on the same
/// <c>ExternalIntegrationsDbContext</c> instance, which
/// <see cref="IHostPro.BuildingBlocks.Infrastructure.Persistence.TenantAwareTransactionScope"/>
/// correctly rejected with <c>NestedUnitOfWorkException</c> on every real
/// auto-publish attempt — confirmed empirically by
/// <c>AirbnbAutoPublishNestedTransactionRegressionTests</c>.
/// <see cref="IAirbnbEmailUnitOfWork"/> remains the correct abstraction for
/// every OTHER consumer in this Bounded Context that never publishes an
/// event (mailbox connect/disconnect, token cache updates) — only this
/// runner's transaction ownership changed.
/// </summary>
public sealed class AirbnbEmailDeltaSyncRunner : IAirbnbEmailDeltaSyncRunner
{
    /// <summary>Hard safety cap — a mailbox with more pages than this in one run stops early and resumes on the next scheduled tick, rather than looping indefinitely.</summary>
    private const int MaxPagesPerRun = 25;

    /// <summary>Parser version recorded on every receipt this runner marks Processed/NeedsReview/Failed/Ignored via the DRY_RUN/publication pipeline — kept here (not read from the parser type) since only the Infrastructure-layer parser implementation may reference it directly, and this runner only depends on the Application-layer evaluator abstraction.</summary>
    private const string ReservationParserVersion = "airbnb-reservation-reminder-v1";
    private const string ReservationDetectedEventType = "RESERVATION_REMINDER";

    /// <summary>Safe, non-PII diagnostic reason recorded when the resolved-property publisher itself throws — never the exception message (which could echo back input).</summary>
    private const string PublisherFailureReason = "PublisherFailure";

    private readonly IAirbnbEmailMailboxConnectionRepository _connectionRepository;
    private readonly IAirbnbEmailSyncStateRepository _syncStateRepository;
    private readonly IAirbnbEmailMessageReceiptRepository _receiptRepository;
    private readonly IAirbnbEmailAuthenticator _authenticator;
    private readonly IAirbnbEmailMessageSource _messageSource;
    private readonly IAirbnbReservationDryRunEvaluator _dryRunEvaluator;
    private readonly IAirbnbResolvedReservationSyncPublisher _resolvedPublisher;
    private readonly IExternalIntegrationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AirbnbEmailDeltaSyncRunner> _logger;

    public AirbnbEmailDeltaSyncRunner(
        IAirbnbEmailMailboxConnectionRepository connectionRepository,
        IAirbnbEmailSyncStateRepository syncStateRepository,
        IAirbnbEmailMessageReceiptRepository receiptRepository,
        IAirbnbEmailAuthenticator authenticator,
        IAirbnbEmailMessageSource messageSource,
        IAirbnbReservationDryRunEvaluator dryRunEvaluator,
        IAirbnbResolvedReservationSyncPublisher resolvedPublisher,
        IExternalIntegrationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<AirbnbEmailDeltaSyncRunner> logger)
    {
        _connectionRepository = connectionRepository;
        _syncStateRepository = syncStateRepository;
        _receiptRepository = receiptRepository;
        _authenticator = authenticator;
        _messageSource = messageSource;
        _dryRunEvaluator = dryRunEvaluator;
        _resolvedPublisher = resolvedPublisher;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(Guid tenantId, string mailFolderId, CancellationToken cancellationToken)
    {
        var connection = await _transactionExecutor.ExecuteAsync(
            () => _connectionRepository.GetForCurrentTenantAsync(cancellationToken), cancellationToken);

        if (connection is null || !connection.IsEnabled || connection.AuthorizationStatus != AirbnbEmailAuthorizationStatus.Connected)
        {
            _logger.LogDebug(
                "Airbnb Email Bridge delta sync skipped for tenant {TenantId} - no enabled, connected mailbox.", tenantId);
            return;
        }

        var connectionId = connection.Id;

        var authOutcome = await _authenticator.AcquireTokenSilentAsync(tenantId, cancellationToken);
        if (!authOutcome.IsSuccess)
        {
            if (authOutcome.ReauthorizationRequired)
            {
                await _transactionExecutor.ExecuteAsync(async () =>
                {
                    var c = await _connectionRepository.GetForCurrentTenantAsync(cancellationToken);
                    c?.MarkError(_timeProvider.GetUtcNow());
                    return true;
                }, cancellationToken);

                _logger.LogWarning(
                    "Airbnb Email Bridge mailbox for tenant {TenantId} requires reauthorization - polling stopped for this connection.",
                    tenantId);
            }
            else
            {
                _logger.LogWarning(
                    "Airbnb Email Bridge silent token acquisition failed transiently for tenant {TenantId} - will retry next cycle.",
                    tenantId);
            }

            return;
        }

        // Ensure the sync-state row exists before any page work, so every
        // later step only ever needs to update it, never decide whether to
        // insert it — avoids re-adding an already-tracked/committed entity.
        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var existing = await _syncStateRepository.GetForCurrentTenantAsync(connectionId, mailFolderId, cancellationToken);
            if (existing is null)
            {
                _syncStateRepository.Add(
                    AirbnbEmailSyncState.Create(Guid.NewGuid(), tenantId, connectionId, mailFolderId, _timeProvider.GetUtcNow()));
            }

            return true;
        }, cancellationToken);

        var cursor = await _transactionExecutor.ExecuteAsync(
            async () => (await _syncStateRepository.GetForCurrentTenantAsync(connectionId, mailFolderId, cancellationToken))!.DeltaLink,
            cancellationToken);

        for (var page = 0; page < MaxPagesPerRun; page++)
        {
            var attemptedAt = _timeProvider.GetUtcNow();
            var fetchOutcome = await _messageSource.GetDeltaPageAsync(authOutcome.AccessToken!, mailFolderId, cursor, cancellationToken);

            if (!fetchOutcome.IsSuccess)
            {
                await _transactionExecutor.ExecuteAsync(async () =>
                {
                    var state = (await _syncStateRepository.GetForCurrentTenantAsync(connectionId, mailFolderId, cancellationToken))!;
                    if (fetchOutcome.FailureReason == AirbnbEmailDeltaFetchFailureReason.InvalidDeltaLink)
                        state.Reset(attemptedAt);
                    else
                        state.RecordFailure(fetchOutcome.SafeErrorCode ?? "unknown", attemptedAt);

                    return true;
                }, cancellationToken);

                _logger.LogWarning(
                    "Airbnb Email Bridge delta fetch failed for tenant {TenantId}: {SafeErrorCode} ({FailureReason}) - cursor not advanced.",
                    tenantId, fetchOutcome.SafeErrorCode, fetchOutcome.FailureReason);
                return;
            }

            var deltaPage = fetchOutcome.Page!;

            // Full body is fetched OUTSIDE the transaction below (an external
            // HTTP call has no place holding a DB transaction open), and only
            // for messages whose sender domain already looks like Airbnb's -
            // never for the rest of the tenant's mail, and never widening the
            // delta page's own $select (Fase 9 review - avoid overfetching).
            var candidateBodies = new Dictionary<string, string?>();
            foreach (var message in deltaPage.Messages)
            {
                var domain = ExtractDomain(message.FromAddress) ?? ExtractDomain(message.SenderAddress);
                if (domain is not null && domain.Contains("airbnb", StringComparison.OrdinalIgnoreCase))
                    candidateBodies[message.MessageId] = await _messageSource.GetMessageBodyAsync(authOutcome.AccessToken!, message.MessageId, cancellationToken);
            }

            await _transactionExecutor.ExecuteAsync(async () =>
            {
                foreach (var message in deltaPage.Messages)
                {
                    if (await _receiptRepository.ExistsForCurrentTenantAsync(message.MessageId, cancellationToken))
                        continue;

                    var receipt = AirbnbEmailMessageReceipt.Create(
                        Guid.NewGuid(), tenantId, message.MessageId, message.InternetMessageId,
                        message.ReceivedAtUtc, _timeProvider.GetUtcNow());
                    _receiptRepository.Add(receipt);

                    if (candidateBodies.TryGetValue(message.MessageId, out var body) && body is not null)
                    {
                        var dryRunOutcome = await _dryRunEvaluator.EvaluateAsync(
                            message.Subject ?? string.Empty, body, message.ReceivedAtUtc, cancellationToken);
                        await ApplyDryRunOutcomeAsync(
                            connection, message.ReceivedAtUtc, tenantId, receipt, dryRunOutcome, _timeProvider.GetUtcNow(), cancellationToken);
                    }
                }

                if (deltaPage.DeltaLink is not null)
                {
                    var state = (await _syncStateRepository.GetForCurrentTenantAsync(connectionId, mailFolderId, cancellationToken))!;
                    state.RecordSuccess(deltaPage.DeltaLink, attemptedAt);
                }

                return true;
            }, cancellationToken);

            _logger.LogInformation(
                "Airbnb Email Bridge delta page processed for tenant {TenantId}: {MessageCount} messages observed, finalPage={IsFinalPage}.",
                tenantId, deltaPage.Messages.Count, deltaPage.DeltaLink is not null);

            if (deltaPage.DeltaLink is not null)
                return;

            cursor = deltaPage.NextLink;
        }

        _logger.LogWarning(
            "Airbnb Email Bridge delta sync for tenant {TenantId} exceeded {MaxPages} pages in one run - stopping early, will resume next cycle.",
            tenantId, MaxPagesPerRun);
    }

    /// <summary>
    /// Maps one DRY_RUN evaluation onto the receipt's existing, already-tested
    /// processing lifecycle (Fase 9 review — Reservation Email Parser gate —
    /// extended by the Automatic Publication Design + Safety gate), reusing
    /// the receipt's existing <see cref="AirbnbEmailMessageProcessingStatus"/>
    /// states plus the one new <c>Ignored</c> value rather than inventing new
    /// domain states for concepts (UNSUPPORTED_TEMPLATE, MAPPING_NOT_FOUND)
    /// the existing Failed/NeedsReview states already capture safely:
    /// <list type="bullet">
    /// <item>parse failure (any reason, including an unrecognized template) → Failed, with the safe failure-reason code as the diagnostic message. Deliberately UNCHANGED by this gate - reliably telling apart a legitimate non-reservation Airbnb email (payment/review/etc.) from a genuinely-unparseable reservation template would require a content classifier this feature has no real evidence for yet, so this bucket is never reclassified to Ignored/NeedsReview here (flagged explicitly, not guessed).</item>
    /// <item>parsed successfully but the listing title has no mapping yet → NeedsReview (a human needs to add the mapping - not an error in the email itself), regardless of <see cref="AirbnbEmailMailboxConnection.AutoPublishEnabled"/> - there is nothing to publish either way.</item>
    /// <item>parsed successfully, the listing resolved to a Property, but <see cref="AirbnbEmailMailboxConnection.AutoPublishEnabled"/> is <c>false</c> (every tenant's default) → Processed, DRY_RUN evidence only - no publisher call, exactly the pre-existing behavior.</item>
    /// <item>same, but AutoPublishEnabled=true AND the message predates <see cref="AirbnbEmailMailboxConnection.AutoPublishNotBeforeUtc"/> → Ignored - historical-import protection, never a bulk-publish of whatever the first delta sync after activation happens to observe.</item>
    /// <item>same, AutoPublishEnabled=true AND at/after the cutoff → the REAL publish: <see cref="IAirbnbResolvedReservationSyncPublisher.PublishReservationImportedAsync"/> is invoked, then Processed. A publisher failure is caught HERE (never rethrown) and marks only THIS receipt Failed - one message's publish failure must never abort the transaction/block the rest of the page.</item>
    /// </list>
    /// </summary>
    private async Task ApplyDryRunOutcomeAsync(
        AirbnbEmailMailboxConnection connection, DateTimeOffset messageReceivedAtUtc, Guid tenantId,
        AirbnbEmailMessageReceipt receipt, AirbnbReservationDryRunOutcome outcome, DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken)
    {
        if (outcome.WouldImport)
        {
            if (!connection.AutoPublishEnabled)
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
                        "Airbnb resolved-property publish failed for tenant {TenantId} - marking this receipt Failed, other messages in this page are unaffected.",
                        tenantId);
                    receipt.MarkFailed(PublisherFailureReason, ReservationParserVersion, processedAtUtc);
                }
            }
        }
        else if (outcome.ParseFailureReason is { } failureReason)
        {
            receipt.MarkFailed(failureReason.ToString(), ReservationParserVersion, processedAtUtc);
        }
        else
        {
            // Parsed successfully (ExternalReservationId/dates/guest count all
            // present) but PropertyResolved=false - the email itself is fine,
            // a tenant just has not mapped this listing title to a Property
            // yet. Never invented/guessed - see AirbnbListingTitleMapping.
            receipt.MarkNeedsReview(ReservationDetectedEventType, ReservationParserVersion, processedAtUtc);
        }
    }

    private static string? ExtractDomain(string? emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
            return null;
        var atIndex = emailAddress.LastIndexOf('@');
        return atIndex >= 0 && atIndex < emailAddress.Length - 1 ? emailAddress[(atIndex + 1)..] : null;
    }
}
