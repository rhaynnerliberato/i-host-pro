using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Projections;

/// <summary>
/// Dashboard &amp; Reporting's own local, tenant-aware read-model row for a
/// single Housekeeping <c>CleaningOccurrence</c> — built exclusively from
/// consuming Housekeeping's own <c>CleaningOccurrenceRegistered</c>
/// Integration Event (Fase 7, Incremento 2 — Dashboard &amp; Reporting
/// Foundation, Checkpoint 1). Append-only, never updated after creation —
/// mirrors the source aggregate's own append-only nature
/// (<c>CleaningOccurrence</c> has no lifecycle/resolution state, Checkpoint
/// 0 decision, §8). Carries no Description/RegisteredByUserName — never
/// published by the source event to begin with. <see cref="PropertyId"/> is
/// deliberately absent here too — resolved, when needed, via
/// <see cref="CleaningId"/> against <c>DashboardCleaningProjectionEntry</c>,
/// never duplicated (Checkpoint 0 decision, §7).
/// </summary>
public sealed class DashboardOccurrenceProjectionEntry : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid OccurrenceId { get; private set; }
    public Guid CleaningId { get; private set; }
    public string Type { get; private set; } = null!;
    public DateTimeOffset RegisteredAtUtc { get; private set; }

    private DashboardOccurrenceProjectionEntry()
    {
        // EF Core materialization.
    }

    public DashboardOccurrenceProjectionEntry(
        Guid tenantId, Guid occurrenceId, Guid cleaningId, string type, DateTimeOffset registeredAtUtc)
    {
        TenantId = tenantId;
        OccurrenceId = occurrenceId;
        CleaningId = cleaningId;
        Type = type;
        RegisteredAtUtc = registeredAtUtc;
    }
}
