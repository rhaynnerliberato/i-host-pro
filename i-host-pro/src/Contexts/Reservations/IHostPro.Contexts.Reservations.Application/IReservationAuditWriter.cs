using IHostPro.Contexts.Reservations.Domain;

namespace IHostPro.Contexts.Reservations.Application;

/// <summary>
/// Stages a single append-only audit entry for this Bounded Context's own
/// transactional audit trail — mirrors
/// <c>PropertyManagement.Application.IPropertyAuditWriter</c> exactly.
/// </summary>
public interface IReservationAuditWriter
{
    void Record(ReservationAuditEntry entry);
}
