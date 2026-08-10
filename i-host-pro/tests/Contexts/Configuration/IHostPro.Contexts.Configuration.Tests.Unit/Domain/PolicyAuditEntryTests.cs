using FluentAssertions;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Domain;

public class PolicyAuditEntryTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public void Create_for_an_initial_version_carries_no_previous_version_or_value()
    {
        var entry = PolicyAuditEntry.Create(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            previousVersion: null, newVersion: 1, previousValue: null, newValue: """{"allowed":true}""",
            authorUserId: Guid.NewGuid(), occurredAtUtc: DateTimeOffset.UtcNow, reason: "initial setup", origin: "Api");

        entry.PreviousVersion.Should().BeNull();
        entry.PreviousValue.Should().BeNull();
        entry.NewVersion.Should().Be(1);
    }

    [Fact]
    public void Create_for_a_subsequent_version_carries_both_previous_and_new_values()
    {
        var entry = PolicyAuditEntry.Create(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            previousVersion: 1, newVersion: 2, previousValue: """{"allowed":true}""", newValue: """{"allowed":false}""",
            authorUserId: Guid.NewGuid(), occurredAtUtc: DateTimeOffset.UtcNow, reason: "policy change", origin: "Api");

        entry.PreviousVersion.Should().Be(1);
        entry.PreviousValue.Should().Be("""{"allowed":true}""");
        entry.NewVersion.Should().Be(2);
        entry.NewValue.Should().Be("""{"allowed":false}""");
    }

    [Fact]
    public void Create_rejects_Global_scope()
    {
        var act = () => PolicyAuditEntry.Create(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Global(),
            null, 1, null, """{"allowed":true}""", Guid.NewGuid(), DateTimeOffset.UtcNow, "reason", "Api");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_an_empty_reason()
    {
        var act = () => PolicyAuditEntry.Create(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            null, 1, null, """{"allowed":true}""", Guid.NewGuid(), DateTimeOffset.UtcNow, reason: " ", origin: "Api");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_an_empty_origin()
    {
        var act = () => PolicyAuditEntry.Create(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            null, 1, null, """{"allowed":true}""", Guid.NewGuid(), DateTimeOffset.UtcNow, reason: "reason", origin: " ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_accepts_optional_session_and_ip_when_present()
    {
        var entry = PolicyAuditEntry.Create(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            null, 1, null, """{"allowed":true}""", Guid.NewGuid(), DateTimeOffset.UtcNow, "reason", "Api",
            sessionId: "session-123", ipAddress: "203.0.113.10");

        entry.SessionId.Should().Be("session-123");
        entry.IpAddress.Should().Be("203.0.113.10");
    }
}
