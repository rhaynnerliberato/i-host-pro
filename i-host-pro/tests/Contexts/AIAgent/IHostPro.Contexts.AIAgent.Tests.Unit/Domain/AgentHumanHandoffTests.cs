using FluentAssertions;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Domain;

/// <summary>Fase 11, Checkpoint 6 (Human Handoff, Safety &amp; Audit) — request/notify/resume lifecycle, invariants. Mirrors <c>AgentPendingActionTests</c>' own structure exactly.</summary>
public class AgentHumanHandoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgentSessionId = Guid.NewGuid();

    private static AgentHumanHandoff RequestValid() =>
        AgentHumanHandoff.Request(Guid.NewGuid(), TenantId, AgentSessionId, AgentHumanHandoffReasonCode.ExplicitHumanRequest, Now);

    [Fact]
    public void Request_with_valid_data_begins_Requested()
    {
        var handoff = RequestValid();

        handoff.TenantId.Should().Be(TenantId);
        handoff.AgentSessionId.Should().Be(AgentSessionId);
        handoff.ReasonCode.Should().Be(AgentHumanHandoffReasonCode.ExplicitHumanRequest);
        handoff.Status.Should().Be(AgentHumanHandoffStatus.Requested);
        handoff.RequestedAtUtc.Should().Be(Now);
        handoff.NotificationAttemptedAtUtc.Should().BeNull();
        handoff.NotifiedAtUtc.Should().BeNull();
        handoff.NotificationFailureCode.Should().BeNull();
        handoff.ResumedAtUtc.Should().BeNull();
        handoff.ResumedByActorId.Should().BeNull();
    }

    [Fact]
    public void Request_rejects_empty_AgentSessionId()
    {
        var act = () => AgentHumanHandoff.Request(Guid.NewGuid(), TenantId, Guid.Empty, AgentHumanHandoffReasonCode.Refund, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkNotificationFailed_records_the_failure_code_and_stays_Requested()
    {
        var handoff = RequestValid();
        var attemptedAt = Now.AddSeconds(1);

        handoff.MarkNotificationFailed(attemptedAt, "connector_exception");

        handoff.Status.Should().Be(AgentHumanHandoffStatus.Requested, "a notification failure never advances the status");
        handoff.NotificationAttemptedAtUtc.Should().Be(attemptedAt);
        handoff.NotificationFailureCode.Should().Be("connector_exception");
        handoff.NotifiedAtUtc.Should().BeNull();
    }

    [Fact]
    public void MarkNotificationFailed_rejects_empty_failure_code()
    {
        var handoff = RequestValid();

        var act = () => handoff.MarkNotificationFailed(Now.AddSeconds(1), "");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkNotificationFailed_throws_when_already_Notified()
    {
        var handoff = RequestValid();
        handoff.MarkNotified(Now.AddSeconds(1));

        var act = () => handoff.MarkNotificationFailed(Now.AddSeconds(2), "connector_exception");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkNotified_transitions_to_Notified_and_clears_any_prior_failure_code()
    {
        var handoff = RequestValid();
        handoff.MarkNotificationFailed(Now.AddSeconds(1), "connector_exception");
        var notifiedAt = Now.AddSeconds(2);

        handoff.MarkNotified(notifiedAt);

        handoff.Status.Should().Be(AgentHumanHandoffStatus.Notified);
        handoff.NotifiedAtUtc.Should().Be(notifiedAt);
        handoff.NotificationAttemptedAtUtc.Should().Be(notifiedAt);
        handoff.NotificationFailureCode.Should().BeNull("a later success clears any earlier recorded failure");
    }

    [Fact]
    public void MarkNotified_throws_when_already_Notified()
    {
        var handoff = RequestValid();
        handoff.MarkNotified(Now.AddSeconds(1));

        var act = () => handoff.MarkNotified(Now.AddSeconds(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resume_from_Requested_transitions_to_Resumed_and_records_actor_and_timestamp()
    {
        var handoff = RequestValid();
        var actorId = Guid.NewGuid();
        var resumedAt = Now.AddMinutes(5);

        handoff.Resume(resumedAt, actorId);

        handoff.Status.Should().Be(AgentHumanHandoffStatus.Resumed);
        handoff.ResumedAtUtc.Should().Be(resumedAt);
        handoff.ResumedByActorId.Should().Be(actorId);
    }

    [Fact]
    public void Resume_from_Notified_transitions_to_Resumed()
    {
        var handoff = RequestValid();
        handoff.MarkNotified(Now.AddSeconds(1));

        handoff.Resume(Now.AddMinutes(5), Guid.NewGuid());

        handoff.Status.Should().Be(AgentHumanHandoffStatus.Resumed);
    }

    [Fact]
    public void Resume_throws_when_already_Resumed()
    {
        var handoff = RequestValid();
        handoff.Resume(Now.AddMinutes(5), Guid.NewGuid());

        var act = () => handoff.Resume(Now.AddMinutes(10), Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resume_rejects_empty_actor_id()
    {
        var handoff = RequestValid();

        var act = () => handoff.Resume(Now.AddMinutes(5), Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }
}
