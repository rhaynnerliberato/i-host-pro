using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application;

/// <summary>
/// Stages a single append-only audit entry for this Bounded Context's own
/// transactional audit trail — mirrors
/// <c>Reservations.Application.IReservationAuditWriter</c> exactly.
/// </summary>
public interface IHousekeepingAuditWriter
{
    void Record(CleaningAuditEntry entry);
}
