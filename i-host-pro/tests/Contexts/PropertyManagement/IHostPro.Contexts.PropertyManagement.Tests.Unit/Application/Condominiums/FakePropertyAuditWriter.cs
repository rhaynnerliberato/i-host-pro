using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakePropertyAuditWriter : IPropertyAuditWriter
{
    public List<PropertyAuditEntry> RecordedEntries { get; } = [];

    public void Record(PropertyAuditEntry entry) => RecordedEntries.Add(entry);
}
