using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Domain;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

internal sealed class FakeReservationAuditWriter : IReservationAuditWriter
{
    public List<ReservationAuditEntry> RecordedEntries { get; } = [];

    public void Record(ReservationAuditEntry entry) => RecordedEntries.Add(entry);
}
