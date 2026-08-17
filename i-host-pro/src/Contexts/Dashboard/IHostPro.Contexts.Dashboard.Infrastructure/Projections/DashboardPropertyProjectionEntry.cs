using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Projections;

/// <summary>
/// Dashboard &amp; Reporting's own local, tenant-aware read-model row for a
/// single Property — built exclusively from consuming Property Management's
/// own <c>PropertyCreated</c>/<c>PropertyActivated</c>/<c>PropertyDeactivated</c>/
/// <c>PropertyArchived</c> Integration Events (Fase 7, Incremento 2 —
/// Dashboard &amp; Reporting Foundation, Checkpoint 1). Deliberately carries
/// only <see cref="Status"/> — the MVP needs active/inactive/archived counts
/// only (Checkpoint 0 decision, §23), never code/name/address/capacity.
/// <c>PropertyUpdated</c> is deliberately NOT consumed — it never changes
/// <see cref="Status"/> (Checkpoint 0 decision, §33: "não consumir eventos
/// sem impacto na projection").
/// </summary>
public sealed class DashboardPropertyProjectionEntry : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string Status { get; private set; } = null!;

    /// <summary>Out-of-order delivery guard — see <c>DashboardReservationProjectionEntry</c>'s own doc comment.</summary>
    public DateTimeOffset LastEventAtUtc { get; private set; }

    private DashboardPropertyProjectionEntry()
    {
        // EF Core materialization.
    }

    public DashboardPropertyProjectionEntry(Guid tenantId, Guid propertyId, string status, DateTimeOffset lastEventAtUtc)
    {
        TenantId = tenantId;
        PropertyId = propertyId;
        Status = status;
        LastEventAtUtc = lastEventAtUtc;
    }

    public void SetStatus(string status, DateTimeOffset eventAtUtc)
    {
        Status = status;
        LastEventAtUtc = eventAtUtc;
    }
}
