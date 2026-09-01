using FluentAssertions;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Domain;

/// <summary>Fase 11, Checkpoint 4 (Write Tools & Response Delivery) — propose/confirm/execute/cancel lifecycle, invariants. Mirrors <c>AgentToolExecutionTests</c>' own structure exactly.</summary>
public class AgentPendingActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgentSessionId = Guid.NewGuid();
    private static readonly Guid ProposedByInteractionId = Guid.NewGuid();
    private const string SanitizedArgumentsJson = """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""";

    private static AgentPendingAction ProposeValid() =>
        AgentPendingAction.Propose(Guid.NewGuid(), TenantId, AgentSessionId, ProposedByInteractionId, "RequestEarlyCheckIn", SanitizedArgumentsJson, Now);

    [Fact]
    public void Propose_with_valid_data_begins_Proposed()
    {
        var pendingAction = ProposeValid();

        pendingAction.TenantId.Should().Be(TenantId);
        pendingAction.AgentSessionId.Should().Be(AgentSessionId);
        pendingAction.ProposedByInteractionId.Should().Be(ProposedByInteractionId);
        pendingAction.ToolName.Should().Be("RequestEarlyCheckIn");
        pendingAction.SanitizedArguments.Should().Be(SanitizedArgumentsJson);
        pendingAction.Status.Should().Be(AgentPendingActionStatus.Proposed);
        pendingAction.CreatedAtUtc.Should().Be(Now);
        pendingAction.ConfirmedAtUtc.Should().BeNull();
        pendingAction.ExecutedAtUtc.Should().BeNull();
        pendingAction.CancelledAtUtc.Should().BeNull();
    }

    [Fact]
    public void Propose_rejects_empty_AgentSessionId()
    {
        var act = () => AgentPendingAction.Propose(Guid.NewGuid(), TenantId, Guid.Empty, ProposedByInteractionId, "RequestEarlyCheckIn", SanitizedArgumentsJson, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Propose_rejects_empty_ProposedByInteractionId()
    {
        var act = () => AgentPendingAction.Propose(Guid.NewGuid(), TenantId, AgentSessionId, Guid.Empty, "RequestEarlyCheckIn", SanitizedArgumentsJson, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Propose_rejects_empty_ToolName()
    {
        var act = () => AgentPendingAction.Propose(Guid.NewGuid(), TenantId, AgentSessionId, ProposedByInteractionId, "", SanitizedArgumentsJson, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Propose_rejects_empty_SanitizedArguments()
    {
        var act = () => AgentPendingAction.Propose(Guid.NewGuid(), TenantId, AgentSessionId, ProposedByInteractionId, "RequestEarlyCheckIn", "", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Confirm_transitions_to_Confirmed_and_records_the_timestamp()
    {
        var pendingAction = ProposeValid();
        var confirmedAt = Now.AddMinutes(1);

        pendingAction.Confirm(confirmedAt);

        pendingAction.Status.Should().Be(AgentPendingActionStatus.Confirmed);
        pendingAction.ConfirmedAtUtc.Should().Be(confirmedAt);
    }

    [Fact]
    public void Confirm_throws_when_not_Proposed()
    {
        var pendingAction = ProposeValid();
        pendingAction.Confirm(Now.AddMinutes(1));

        var act = () => pendingAction.Confirm(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkExecuted_transitions_to_Executed_and_records_the_timestamp()
    {
        var pendingAction = ProposeValid();
        pendingAction.Confirm(Now.AddMinutes(1));
        var executedAt = Now.AddMinutes(2);

        pendingAction.MarkExecuted(executedAt);

        pendingAction.Status.Should().Be(AgentPendingActionStatus.Executed);
        pendingAction.ExecutedAtUtc.Should().Be(executedAt);
    }

    [Fact]
    public void MarkExecuted_throws_when_not_Confirmed()
    {
        var pendingAction = ProposeValid();

        var act = () => pendingAction.MarkExecuted(Now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_from_Proposed_transitions_to_Cancelled_and_records_the_timestamp()
    {
        var pendingAction = ProposeValid();
        var cancelledAt = Now.AddMinutes(1);

        pendingAction.Cancel(cancelledAt);

        pendingAction.Status.Should().Be(AgentPendingActionStatus.Cancelled);
        pendingAction.CancelledAtUtc.Should().Be(cancelledAt);
    }

    [Fact]
    public void Cancel_from_Confirmed_transitions_to_Cancelled()
    {
        var pendingAction = ProposeValid();
        pendingAction.Confirm(Now.AddMinutes(1));

        pendingAction.Cancel(Now.AddMinutes(2));

        pendingAction.Status.Should().Be(AgentPendingActionStatus.Cancelled);
    }

    [Fact]
    public void Cancel_throws_when_already_Executed()
    {
        var pendingAction = ProposeValid();
        pendingAction.Confirm(Now.AddMinutes(1));
        pendingAction.MarkExecuted(Now.AddMinutes(2));

        var act = () => pendingAction.Cancel(Now.AddMinutes(3));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_throws_when_already_Cancelled()
    {
        var pendingAction = ProposeValid();
        pendingAction.Cancel(Now.AddMinutes(1));

        var act = () => pendingAction.Cancel(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }
}
