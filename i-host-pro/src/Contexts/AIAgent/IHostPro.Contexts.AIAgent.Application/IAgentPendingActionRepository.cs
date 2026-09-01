using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>Fase 11, Checkpoint 4.</summary>
public interface IAgentPendingActionRepository
{
    void Add(AgentPendingAction pendingAction);

    void Update(AgentPendingAction pendingAction);

    Task<AgentPendingAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The single lookup the orchestrator needs — the "active" pending
    /// action (<see cref="AgentPendingActionStatus.Proposed"/> or
    /// <see cref="AgentPendingActionStatus.Confirmed"/>) for a session, if
    /// any. At most one exists per <see cref="AgentSession"/>, backstopped
    /// by a partial unique index (CP4 mandate item 14).
    /// </summary>
    Task<AgentPendingAction?> GetActiveByAgentSessionIdAsync(Guid agentSessionId, CancellationToken cancellationToken);
}
