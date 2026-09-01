using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>Fase 11, Checkpoint 6.</summary>
public interface IAgentHumanHandoffRepository
{
    void Add(AgentHumanHandoff handoff);

    void Update(AgentHumanHandoff handoff);

    Task<AgentHumanHandoff?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The single lookup the orchestrator/Resume flow needs — the "active"
    /// handoff (<see cref="AgentHumanHandoffStatus.Requested"/> or
    /// <see cref="AgentHumanHandoffStatus.Notified"/>) for a session, if any.
    /// At most one exists per <see cref="AgentSession"/>, backstopped by a
    /// partial unique index (CP6 mandate item 10).
    /// </summary>
    Task<AgentHumanHandoff?> GetActiveByAgentSessionIdAsync(Guid agentSessionId, CancellationToken cancellationToken);
}
