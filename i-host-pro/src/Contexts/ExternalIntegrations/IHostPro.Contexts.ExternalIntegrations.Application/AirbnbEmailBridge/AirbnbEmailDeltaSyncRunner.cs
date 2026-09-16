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

    private readonly IAirbnbEmailMailboxConnectionRepository _connectionRepository;
    private readonly IAirbnbEmailSyncStateRepository _syncStateRepository;
    private readonly IAirbnbEmailMessageReceiptRepository _receiptRepository;
    private readonly IAirbnbEmailAuthenticator _authenticator;
    private readonly IAirbnbEmailMessageSource _messageSource;
    private readonly IAirbnbEmailUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AirbnbEmailDeltaSyncRunner> _logger;

    public AirbnbEmailDeltaSyncRunner(
        IAirbnbEmailMailboxConnectionRepository connectionRepository,
        IAirbnbEmailSyncStateRepository syncStateRepository,
        IAirbnbEmailMessageReceiptRepository receiptRepository,
        IAirbnbEmailAuthenticator authenticator,
        IAirbnbEmailMessageSource messageSource,
        IAirbnbEmailUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<AirbnbEmailDeltaSyncRunner> logger)
    {
        _connectionRepository = connectionRepository;
        _syncStateRepository = syncStateRepository;
        _receiptRepository = receiptRepository;
        _authenticator = authenticator;
        _messageSource = messageSource;
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

            await _unitOfWork.ExecuteAsync(async () =>
            {
                foreach (var message in deltaPage.Messages)
                {
                    if (await _receiptRepository.ExistsForCurrentTenantAsync(message.MessageId, cancellationToken))
                        continue;

                    _receiptRepository.Add(AirbnbEmailMessageReceipt.Create(
                        Guid.NewGuid(), tenantId, message.MessageId, message.InternetMessageId,
                        message.ReceivedAtUtc, _timeProvider.GetUtcNow()));
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
}
