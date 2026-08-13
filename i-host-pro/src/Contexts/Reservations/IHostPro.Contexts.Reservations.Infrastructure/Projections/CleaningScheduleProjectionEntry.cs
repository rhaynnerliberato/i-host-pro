using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Reservations.Infrastructure.Projections;

/// <summary>
/// This context's (Reservation &amp; Scheduling, implementation
/// <c>Reservations</c>) own local, tenant-aware read-model row for a single
/// Housekeeping Cleaning — built exclusively from consuming Housekeeping's
/// own Cleaning lifecycle Integration Events (Fase 7, Incremento 1 — Agenda
/// Foundation, Checkpoint 1). Deliberately carries only the fields the
/// Agenda's <c>ScheduleItem</c> read shape displays — no occurrence
/// description, checklist, audit trail, or any other Housekeeping-internal
/// data. A physically separate table from <c>housekeeping.cleanings</c>,
/// never a foreign key across contexts — same opaque-Guid convention already
/// used everywhere else in this platform. Infrastructure-only persistence
/// model (never referenced from Application/Domain).
/// </summary>
public sealed class CleaningScheduleProjectionEntry : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid CleaningId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid? AssignedHousekeeperUserId { get; private set; }
    public DateTimeOffset? ScheduledAtUtc { get; private set; }
    public string Status { get; private set; } = null!;

    private CleaningScheduleProjectionEntry()
    {
        // EF Core materialization.
    }

    public CleaningScheduleProjectionEntry(
        Guid tenantId, Guid cleaningId, Guid propertyId, string status, DateTimeOffset? scheduledAtUtc)
    {
        TenantId = tenantId;
        CleaningId = cleaningId;
        PropertyId = propertyId;
        Status = status;
        ScheduledAtUtc = scheduledAtUtc;
    }

    public void SetAssignedHousekeeper(Guid housekeeperUserId) => AssignedHousekeeperUserId = housekeeperUserId;

    public void SetStatus(string status) => Status = status;
}
