using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Projections;

/// <summary>
/// Dashboard &amp; Reporting's own local, tenant-aware read-model row for a
/// single Housekeeping Cleaning — built exclusively from consuming
/// Housekeeping's own ten Cleaning lifecycle Integration Events (Fase 7,
/// Incremento 2 — Dashboard &amp; Reporting Foundation, Checkpoint 1).
/// Mirrors <c>CleaningScheduleProjectionEntry</c>'s own precedent — a
/// physically separate table from <c>housekeeping.cleanings</c>, never a
/// foreign key across contexts.
///
/// <see cref="StartedAtUtc"/>/<see cref="CompletedAtUtc"/> (Checkpoint 0
/// decision, §22) capture <c>IntegrationEvent.Timestamp</c> from
/// <c>CleaningStarted</c>/<c>CleaningCompleted</c> respectively — both
/// events are enqueued synchronously in the same handler call as the
/// domain's own status transition (confirmed against
/// <c>StartCleaningCommandHandler</c>/<c>CompleteCleaningCommandHandler</c>),
/// so the envelope timestamp is a reasonable operational proxy for "when the
/// transition happened". Stored for future diagnostic/operational use ONLY
/// — this checkpoint does not compute or expose any duration/SLA metric from
/// them (Checkpoint 0 decision, §20: duration KPIs deferred).
/// </summary>
public sealed class DashboardCleaningProjectionEntry : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid CleaningId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid? HousekeeperUserId { get; private set; }
    public DateTimeOffset? ScheduledAtUtc { get; private set; }
    public string Status { get; private set; } = null!;
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>
    /// Set exclusively by <c>CleaningCancelled.Timestamp</c> (Fase 7,
    /// Incremento 2, Checkpoint 2 — Overview API's <c>CancelledInPeriod</c>
    /// indicator needs an objective cancellation instant, not just the
    /// current <see cref="Status"/>). Backfilled by the bootstrap step
    /// directly from <c>housekeeping.cleanings.cancelled_at_utc</c> — a real,
    /// dedicated column the <c>Cleaning</c> aggregate already maintains.
    /// </summary>
    public DateTimeOffset? CancelledAtUtc { get; private set; }

    /// <summary>Out-of-order delivery guard — see <c>DashboardReservationProjectionEntry</c>'s own doc comment.</summary>
    public DateTimeOffset LastEventAtUtc { get; private set; }

    private DashboardCleaningProjectionEntry()
    {
        // EF Core materialization.
    }

    public DashboardCleaningProjectionEntry(
        Guid tenantId, Guid cleaningId, Guid propertyId, string status,
        DateTimeOffset? scheduledAtUtc, DateTimeOffset lastEventAtUtc)
    {
        TenantId = tenantId;
        CleaningId = cleaningId;
        PropertyId = propertyId;
        Status = status;
        ScheduledAtUtc = scheduledAtUtc;
        LastEventAtUtc = lastEventAtUtc;
    }

    public void SetAssignedHousekeeper(Guid housekeeperUserId) => HousekeeperUserId = housekeeperUserId;

    public void SetStarted(DateTimeOffset startedAtUtc) => StartedAtUtc = startedAtUtc;

    public void SetCompleted(DateTimeOffset completedAtUtc) => CompletedAtUtc = completedAtUtc;

    public void SetCancelled(DateTimeOffset cancelledAtUtc) => CancelledAtUtc = cancelledAtUtc;

    public void SetStatus(string status, DateTimeOffset eventAtUtc)
    {
        Status = status;
        LastEventAtUtc = eventAtUtc;
    }
}
