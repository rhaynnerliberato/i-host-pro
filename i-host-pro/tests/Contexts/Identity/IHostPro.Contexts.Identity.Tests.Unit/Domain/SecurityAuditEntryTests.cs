using FluentAssertions;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Tests.Unit.Domain;

public class SecurityAuditEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Record_creates_an_entry_with_only_the_required_fields()
    {
        var tenantId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var entry = SecurityAuditEntry.Record(
            Guid.NewGuid(), tenantId, SecurityAuditEventType.LogoutSucceeded, Now, correlationId);

        entry.TenantId.Should().Be(tenantId);
        entry.EventType.Should().Be(SecurityAuditEventType.LogoutSucceeded);
        entry.OccurredAt.Should().Be(Now);
        entry.CorrelationId.Should().Be(correlationId);
        entry.ReasonCode.Should().BeNull();
        entry.UserId.Should().BeNull();
        // Fase 12, Checkpoint 4 — a nullable ActorId is exactly what lets pre-
        // migration (historical) rows keep loading without a fabricated actor.
        entry.ActorId.Should().BeNull();
        entry.SessionId.Should().BeNull();
        entry.RefreshTokenId.Should().BeNull();
        entry.IpAddress.Should().BeNull();
    }

    [Fact]
    public void Record_creates_an_entry_with_every_optional_field_populated()
    {
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var refreshTokenId = Guid.NewGuid();

        var entry = SecurityAuditEntry.Record(
            Guid.NewGuid(),
            Guid.NewGuid(),
            SecurityAuditEventType.RefreshRejected,
            Now,
            Guid.NewGuid(),
            reasonCode: SecurityAuditReasonCode.SessionNotActive,
            userId: userId,
            actorId: actorId,
            sessionId: sessionId,
            refreshTokenId: refreshTokenId,
            ipAddress: "203.0.113.7");

        entry.ReasonCode.Should().Be(SecurityAuditReasonCode.SessionNotActive);
        entry.UserId.Should().Be(userId);
        entry.ActorId.Should().Be(actorId);
        entry.SessionId.Should().Be(sessionId);
        entry.RefreshTokenId.Should().Be(refreshTokenId);
        entry.IpAddress.Should().Be("203.0.113.7");
    }

    [Fact]
    public void Record_accepts_a_distinct_ActorId_from_UserId_never_conflating_actor_and_target()
    {
        // Fase 12, Checkpoint 4, mandate item 14 — proves at the domain level
        // that ActorId (who performed the operation) and UserId (who it was
        // performed on) are two independent optional fields, never the same
        // slot: an administrative operation on another user must be able to
        // record both simultaneously, distinctly.
        var actorId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        var entry = SecurityAuditEntry.Record(
            Guid.NewGuid(), Guid.NewGuid(), SecurityAuditEventType.UserBlocked, Now, Guid.NewGuid(),
            userId: targetUserId, actorId: actorId);

        entry.ActorId.Should().Be(actorId);
        entry.UserId.Should().Be(targetUserId);
        (entry.ActorId == entry.UserId).Should().BeFalse();
    }

    [Fact]
    public void Record_rejects_an_empty_tenant_id()
    {
        var act = () => SecurityAuditEntry.Record(
            Guid.NewGuid(), Guid.Empty, SecurityAuditEventType.LoginSucceeded, Now, Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Record_rejects_an_empty_correlation_id()
    {
        var act = () => SecurityAuditEntry.Record(
            Guid.NewGuid(), Guid.NewGuid(), SecurityAuditEventType.LoginSucceeded, Now, Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SecurityAuditEntry_declares_no_instance_mutator()
    {
        // Structural guarantee, not a business-rule test: an audit trail that
        // could be edited after the fact would not be a trail (Incremento 2
        // plan, ajuste 5). A hypothetical future mutator (like Session.Revoke
        // or User.Block) would be a public, declared, non-getter instance
        // method — this asserts none exists, on top of the compile-time
        // guarantee that every property setter is private.
        var instanceMutators = typeof(SecurityAuditEntry)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // excludes property getters/setters and operator overloads
            .Select(m => m.Name);

        instanceMutators.Should().BeEmpty();
    }
}
