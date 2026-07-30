using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeSecurityAuditWriter : ISecurityAuditWriter
{
    public List<SecurityAuditEntry> RecordedEntries { get; } = [];

    public void Record(SecurityAuditEntry entry) => RecordedEntries.Add(entry);
}
