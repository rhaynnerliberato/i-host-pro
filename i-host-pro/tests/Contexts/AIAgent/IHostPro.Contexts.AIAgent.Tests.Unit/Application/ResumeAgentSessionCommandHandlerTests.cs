using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Application;

/// <summary>Fase 11, Checkpoint 6 — manual Resume, atomic AgentSession+AgentHumanHandoff transition, error cases.</summary>
public class ResumeAgentSessionCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private static AgentSession NewEscalatedSession(Guid id)
    {
        var session = AgentSession.Create(id, TenantId, ConversationId, ReservationId, Now);
        session.Escalate(Now.AddMinutes(1));
        return session;
    }

    [Fact]
    public async Task Handle_resumes_an_escalated_session_with_an_active_handoff()
    {
        var sessionId = Guid.NewGuid();
        var session = NewEscalatedSession(sessionId);
        var handoff = AgentHumanHandoff.Request(Guid.NewGuid(), TenantId, sessionId, AgentHumanHandoffReasonCode.Refund, Now);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(session);
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(handoff);
        var actorId = Guid.NewGuid();
        var handler = new ResumeAgentSessionCommandHandler(
            sessionRepository, handoffRepository, new PassThroughAIAgentTransactionExecutor(), TimeProvider.System);

        var result = await handler.Handle(
            new ResumeAgentSessionCommand { TenantId = TenantId, AgentSessionId = sessionId, ActorId = actorId }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AgentSessionId.Should().Be(sessionId);
        result.Value.AgentHumanHandoffId.Should().Be(handoff.Id);

        session.Status.Should().Be(AgentSessionStatus.Active);
        handoff.Status.Should().Be(AgentHumanHandoffStatus.Resumed);
        handoff.ResumedByActorId.Should().Be(actorId);
    }

    [Fact]
    public async Task Handle_fails_when_the_session_does_not_exist()
    {
        var sessionRepository = new FakeAgentSessionRepository();
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(null);
        var handler = new ResumeAgentSessionCommandHandler(
            sessionRepository, handoffRepository, new PassThroughAIAgentTransactionExecutor(), TimeProvider.System);

        var result = await handler.Handle(
            new ResumeAgentSessionCommand { TenantId = TenantId, AgentSessionId = Guid.NewGuid(), ActorId = Guid.NewGuid() }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AgentSessionNotFound");
    }

    [Fact]
    public async Task Handle_fails_when_the_session_belongs_to_a_different_tenant()
    {
        var sessionId = Guid.NewGuid();
        var session = NewEscalatedSession(sessionId);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(session);
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(null);
        var handler = new ResumeAgentSessionCommandHandler(
            sessionRepository, handoffRepository, new PassThroughAIAgentTransactionExecutor(), TimeProvider.System);

        var result = await handler.Handle(
            new ResumeAgentSessionCommand { TenantId = Guid.NewGuid(), AgentSessionId = sessionId, ActorId = Guid.NewGuid() }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("AgentSessionNotFound", "cross-tenant access must fail exactly like a not-found session, never leak existence");
    }

    [Fact]
    public async Task Handle_fails_when_no_active_handoff_exists_for_an_Active_session()
    {
        var sessionId = Guid.NewGuid();
        var session = AgentSession.Create(sessionId, TenantId, ConversationId, ReservationId, Now);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(session);
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(null);
        var handler = new ResumeAgentSessionCommandHandler(
            sessionRepository, handoffRepository, new PassThroughAIAgentTransactionExecutor(), TimeProvider.System);

        var result = await handler.Handle(
            new ResumeAgentSessionCommand { TenantId = TenantId, AgentSessionId = sessionId, ActorId = Guid.NewGuid() }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NoActiveHumanHandoff");
        session.Status.Should().Be(AgentSessionStatus.Active, "a failed resume must never mutate the session");
    }

    [Fact]
    public async Task Handle_fails_when_the_handoff_was_already_resumed()
    {
        var sessionId = Guid.NewGuid();
        var session = NewEscalatedSession(sessionId);
        var handoff = AgentHumanHandoff.Request(Guid.NewGuid(), TenantId, sessionId, AgentHumanHandoffReasonCode.Refund, Now);
        handoff.Resume(Now.AddMinutes(2), Guid.NewGuid());
        var sessionRepository = FakeAgentSessionRepository.WithExisting(session);
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(null);
        var handler = new ResumeAgentSessionCommandHandler(
            sessionRepository, handoffRepository, new PassThroughAIAgentTransactionExecutor(), TimeProvider.System);

        var result = await handler.Handle(
            new ResumeAgentSessionCommand { TenantId = TenantId, AgentSessionId = sessionId, ActorId = Guid.NewGuid() }, CancellationToken.None);

        result.IsFailure.Should().BeTrue("the handoff is no longer active — GetActiveByAgentSessionIdAsync never returns an already-Resumed one");
        result.Error.Code.Should().Be("NoActiveHumanHandoff");
    }
}
