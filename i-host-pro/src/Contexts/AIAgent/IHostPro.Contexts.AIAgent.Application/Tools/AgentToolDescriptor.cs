namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// The provider-neutral shape a Read Tool advertises to <see cref="IModelProvider"/>
/// (Fase 11, Checkpoint 3, mandate item 9) — deliberately minimal, Name +
/// Description only. Never a provider-specific JSON schema (e.g. Anthropic's
/// own tool-definition shape) — that translation, if ever needed, belongs to
/// a future real provider's own Infrastructure adapter, not this contract.
/// </summary>
public sealed record AgentToolDescriptor(string Name, string Description);
