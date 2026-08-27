using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Housekeeping's own reaction to <see cref="LateCheckoutApproved"/> (Fase
/// 10, Checkpoint 3 — Early Check-in / Late Checkout mandate; ADR-020 second
/// consumer alongside Workflow Orchestration's own reschedule orchestrator).
/// Gated on <see cref="LateCheckoutApproved.UpdatesCleaning"/> — a silent
/// no-op when <c>false</c>. Deliberately does NOT mutate
/// <see cref="Cleaning.ScheduledAtUtc"/> — the mandate explicitly forbids
/// inventing a schedule-offset calculation with no concrete rule defined in
/// Documento 10 (mirrors <c>CreateCleaningForReservationCommandHandler</c>'s
/// own "ScheduledAtUtc is always null — out of scope until Fase 10" note,
/// which this checkpoint still does not resolve). Records only a
/// <see cref="CleaningAuditEntry"/> against the automated Cleaning, if one
/// already exists for the Reservation — proving a real, wired event/reaction
/// pipeline without inventing business logic the documentation does not
/// define. No automated Cleaning found is ALSO a silent no-op — never an
/// error, since no invented Cleaning is created here either.
/// </summary>
public sealed class LateCheckoutApprovedCleaningReactor : IIntegrationEventHandler<LateCheckoutApproved>
{
    public const string AuditActionCode = "late_checkout_approved";

    private readonly ICleaningReader _cleaningReader;
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly IHousekeepingTransactionExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LateCheckoutApprovedCleaningReactor> _logger;

    public LateCheckoutApprovedCleaningReactor(
        ICleaningReader cleaningReader,
        IHousekeepingAuditWriter auditWriter,
        IHousekeepingTransactionExecutor executor,
        TimeProvider timeProvider,
        ILogger<LateCheckoutApprovedCleaningReactor> logger)
    {
        _cleaningReader = cleaningReader;
        _auditWriter = auditWriter;
        _executor = executor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(LateCheckoutApproved @event, CancellationToken cancellationToken)
    {
        if (!@event.UpdatesCleaning)
        {
            _logger.LogInformation(
                "LateCheckoutApproved for tenant {TenantId} reservation {ReservationId}: no-op (UpdatesCleaning is false)",
                @event.TenantId, @event.ReservationId);
            return;
        }

        await _executor.ExecuteAsync(async () =>
        {
            var cleaningId = await _cleaningReader.GetAutomatedCleaningIdByReservationIdAsync(
                @event.TenantId, @event.ReservationId, cancellationToken);

            if (cleaningId is null)
            {
                _logger.LogInformation(
                    "LateCheckoutApproved for tenant {TenantId} reservation {ReservationId}: no-op (no automated Cleaning found)",
                    @event.TenantId, @event.ReservationId);
                return true;
            }

            var now = _timeProvider.GetUtcNow();

            _auditWriter.Record(CleaningAuditEntry.Create(
                Guid.NewGuid(), @event.TenantId, actorUserId: null, "Cleaning", cleaningId.Value,
                AuditActionCode, changedFields: [], now));

            _logger.LogInformation(
                "LateCheckoutApproved for tenant {TenantId} reservation {ReservationId}: recorded audit entry on Cleaning {CleaningId}",
                @event.TenantId, @event.ReservationId, cleaningId.Value);

            return true;
        }, cancellationToken);
    }
}
