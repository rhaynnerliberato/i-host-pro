using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Orchestrates one incremental mailbox synchronization attempt for one
/// tenant (Fase 9 review — Delta Polling gate). DRY_RUN by construction this
/// gate: creates <see cref="AirbnbEmailMessageReceipt"/> rows only — never
/// calls <c>IAirbnbReservationSyncPublisher</c>, never mutates a reservation.
///
/// Consistency model (Fase 9 review §9-10/§36): every database write is its
/// own committed transaction via <see cref="IAirbnbEmailUnitOfWork"/>,
/// re-reading the sync state fresh each time rather than threading a tracked
/// entity across calls. The delta cursor is only ever advanced
/// (<see cref="AirbnbEmailSyncState.RecordSuccess"/>) in the SAME transaction
/// that observes the final page (<c>DeltaLink</c> present) — a failure on any
/// earlier or later page leaves the cursor exactly where it was before this
/// run, and replaying from the same cursor is always safe because
/// <see cref="IAirbnbEmailMessageReceiptRepository.ExistsForCurrentTenantAsync"/>
/// makes re-observing an already-receipted message a no-op.
/// </summary>
public sealed class AirbnbEmailDeltaSyncRunner : IAirbnbEmailDeltaSyncRunner
{
    /// <summary>Hard safety cap — a mailbox with more pages than this in one run stops early and resumes on the next scheduled tick, rather than looping indefinitely.</summary>
    private const int MaxPagesPerRun = 25;

    /// <summary>Parser version recorded on every receipt this runner marks Processed/NeedsReview/Failed via the DRY_RUN pipeline — kept here (not read from the parser type) since only the Infrastructure-layer parser implementation may reference it directly, and this runner only depends on the Application-layer evaluator abstraction.</summary>
    private const string ReservationParserVersion = "airbnb-reservation-reminder-v1";
    private const string ReservationDetectedEventType = "RESERVATION_REMINDER";

    private readonly IAirbnbEmailMailboxConnectionRepository _connectionRepository;
    private readonly IAirbnbEmailSyncStateRepository _syncStateRepository;
    private readonly IAirbnbEmailMessageReceiptRepository _receiptRepository;
    private readonly IAirbnbEmailAuthenticator _authenticator;
    private readonly IAirbnbEmailMessageSource _messageSource;
    private readonly IAirbnbReservationDryRunEvaluator _dryRunEvaluator;
    private readonly IAirbnbEmailUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AirbnbEmailDeltaSyncRunner> _logger;

    public AirbnbEmailDeltaSyncRunner(
        IAirbnbEmailMailboxConnectionRepository connectionRepository,
        IAirbnbEmailSyncStateRepository syncStateRepository,
        IAirbnbEmailMessageReceiptRepository receiptRepository,
        IAirbnbEmailAuthenticator authenticator,
        IAirbnbEmailMessageSource messageSource,
        IAirbnbReservationDryRunEvaluator dryRunEvaluator,
        IAirbnbEmailUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<AirbnbEmailDeltaSyncRunner> logger)
    {
        _connectionRepository = connectionRepository;
        _syncStateRepository = syncStateRepository;
        _receiptRepository = receiptRepository;
        _authenticator = authenticator;
        _messageSource = messageSource;
        _dryRunEvaluator = dryRunEvaluator;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(Guid tenantId, string mailFolderId, CancellationToken cancellationToken)
    {
        var connection = await _unitOfWork.ExecuteAsync(
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
                await _unitOfWork.ExecuteAsync(async () =>
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
        await _unitOfWork.ExecuteAsync(async () =>
        {
            var existing = await _syncStateRepository.GetForCurrentTenantAsync(connectionId, mailFolderId, cancellationToken);
            if (existing is null)
            {
                _syncStateRepository.Add(
                    AirbnbEmailSyncState.Create(Guid.NewGuid(), tenantId, connectionId, mailFolderId, _timeProvider.GetUtcNow()));
            }

            return true;
        }, cancellationToken);

        var cursor = await _unitOfWork.ExecuteAsync(
            async () => (await _syncStateRepository.GetForCurrentTenantAsync(connectionId, mailFolderId, cancellationToken))!.DeltaLink,
            cancellationToken);

        for (var page = 0; page < MaxPagesPerRun; page++)
        {
            var attemptedAt = _timeProvider.GetUtcNow();
            var fetchOutcome = await _messageSource.GetDeltaPageAsync(authOutcome.AccessToken!, mailFolderId, cursor, cancellationToken);

            if (!fetchOutcome.IsSuccess)
            {
                await _unitOfWork.ExecuteAsync(async () =>
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

            await _unitOfWork.ExecuteAsync(async () =>
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
                        ApplyDryRunOutcome(receipt, dryRunOutcome, _timeProvider.GetUtcNow());
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
    /// processing lifecycle (Fase 9 review — Reservation Email Parser
    /// gate) — deliberately reuses the receipt's current 4-state
    /// <see cref="AirbnbEmailMessageProcessingStatus"/> rather than inventing
    /// new domain states for concepts (UNSUPPORTED_TEMPLATE, MAPPING_NOT_FOUND)
    /// that the existing Failed/NeedsReview states, combined with the
    /// receipt's own free-text diagnostic fields, already capture safely:
    /// <list type="bullet">
    /// <item>parse failure (any reason, including an unrecognized template) → Failed, with the safe failure-reason code as the diagnostic message</item>
    /// <item>parsed successfully but the listing title has no mapping yet → NeedsReview (a human needs to add the mapping - not an error in the email itself)</item>
    /// <item>parsed successfully and the listing resolved to a Property → Processed, carrying the parsed confirmation code — this is DRY_RUN evidence only, no publisher/mutation follows</item>
    /// </list>
    /// </summary>
    private static void ApplyDryRunOutcome(AirbnbEmailMessageReceipt receipt, AirbnbReservationDryRunOutcome outcome, DateTimeOffset processedAtUtc)
    {
        if (outcome.WouldImport)
        {
            receipt.MarkProcessed(ReservationDetectedEventType, outcome.ExternalReservationId, ReservationParserVersion, processedAtUtc);
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
