using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakeHousekeepingAuditWriter : IHousekeepingAuditWriter
{
    public List<CleaningAuditEntry> RecordedEntries { get; } = [];

    public void Record(CleaningAuditEntry entry) => RecordedEntries.Add(entry);
}
