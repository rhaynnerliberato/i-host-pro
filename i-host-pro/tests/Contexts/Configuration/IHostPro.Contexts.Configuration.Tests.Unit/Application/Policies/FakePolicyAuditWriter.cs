using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

internal sealed class FakePolicyAuditWriter : IPolicyAuditWriter
{
    public List<PolicyAuditEntry> RecordedEntries { get; } = [];

    public void Record(PolicyAuditEntry entry) => RecordedEntries.Add(entry);
}
