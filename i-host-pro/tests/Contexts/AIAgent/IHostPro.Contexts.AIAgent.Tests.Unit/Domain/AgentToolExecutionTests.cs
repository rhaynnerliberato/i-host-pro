using FluentAssertions;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Domain;

/// <summary>Fase 11, Checkpoint 3 (Read Tools & Context Builder) — success, failure, invariants. Mirrors <c>AgentInteractionTests</c>' own structure exactly.</summary>
public class AgentToolExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgentInteractionId = Guid.NewGuid();

    private static AgentToolExecution StartValid() =>
        AgentToolExecution.Start(Guid.NewGuid(), TenantId, AgentInteractionId, "GetReservationSummary", Now);

    [Fact]
    public void Start_with_valid_data_begins_InProgress()
    {
        var execution = StartValid();

        execution.TenantId.Should().Be(TenantId);
        execution.AgentInteractionId.Should().Be(AgentInteractionId);
        execution.ToolName.Should().Be("GetReservationSummary");
        execution.StartedAtUtc.Should().Be(Now);
        execution.Outcome.Should().Be(AgentToolExecutionOutcome.InProgress);
        execution.CompletedAtUtc.Should().BeNull();
        execution.DurationMs.Should().BeNull();
        execution.FailureCode.Should().BeNull();
    }

    [Fact]
    public void Start_rejects_empty_AgentInteractionId()
    {
        var act = () => AgentToolExecution.Start(Guid.NewGuid(), TenantId, Guid.Empty, "GetReservationSummary", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Start_rejects_empty_ToolName()
    {
        var act = () => AgentToolExecution.Start(Guid.NewGuid(), TenantId, AgentInteractionId, "", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CompleteSuccessfully_records_Success_outcome_and_computes_DurationMs()
    {
        var execution = StartValid();
        var completedAt = Now.AddMilliseconds(250);

        execution.CompleteSuccessfully(completedAt);

        execution.Outcome.Should().Be(AgentToolExecutionOutcome.Success);
        execution.CompletedAtUtc.Should().Be(completedAt);
        execution.DurationMs.Should().Be(250);
        execution.FailureCode.Should().BeNull();
    }

    [Fact]
    public void CompleteWithFailure_records_Failure_outcome_with_the_sanitized_code()
    {
        var execution = StartValid();
        var completedAt = Now.AddMilliseconds(100);

        execution.CompleteWithFailure(completedAt, "reservation_not_found");

        execution.Outcome.Should().Be(AgentToolExecutionOutcome.Failure);
        execution.CompletedAtUtc.Should().Be(completedAt);
        execution.DurationMs.Should().Be(100);
        execution.FailureCode.Should().Be("reservation_not_found");
    }

    [Fact]
    public void CompleteSuccessfully_throws_when_already_completed()
    {
        var execution = StartValid();
        execution.CompleteWithFailure(Now.AddSeconds(1), "some_code");

        var act = () => execution.CompleteSuccessfully(Now.AddSeconds(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CompleteWithFailure_throws_when_already_completed()
    {
        var execution = StartValid();
        execution.CompleteSuccessfully(Now.AddSeconds(1));

        var act = () => execution.CompleteWithFailure(Now.AddSeconds(2), "some_code");

        act.Should().Throw<InvalidOperationException>();
    }
}
