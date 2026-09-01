using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Manually resumes an <see cref="Domain.AgentSession"/> that a real
/// <see cref="Domain.AgentHumanHandoff"/> escalated (Fase 11, Checkpoint 6,
/// mandate item 35/39) — the CP0 <c>HumanHandoffResume=MANUAL ONLY</c>
/// decision's own Application Command. <see cref="ActorId"/> is always the
/// authenticated caller's own id (never a request-body-supplied value) —
/// dispatched exclusively through <see cref="IAIAgentRequestDispatcher"/>
/// from <c>IHostPro.Api</c>'s Resume-session endpoint (guarded by the
/// <c>AI_AGENT:MANAGE</c> permission).
/// </summary>
public sealed record ResumeAgentSessionCommand : ICommand<ResumeAgentSessionResult>
{
    public required Guid TenantId { get; init; }

    public required Guid AgentSessionId { get; init; }

    public required Guid ActorId { get; init; }
}
