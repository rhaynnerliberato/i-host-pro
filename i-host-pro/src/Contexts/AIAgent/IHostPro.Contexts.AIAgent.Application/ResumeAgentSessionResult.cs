namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>Fase 11, Checkpoint 6.</summary>
public sealed record ResumeAgentSessionResult(Guid AgentSessionId, Guid AgentHumanHandoffId, DateTimeOffset ResumedAtUtc);
