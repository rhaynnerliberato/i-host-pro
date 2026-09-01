namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// Implemented by the write Tools whose <see cref="IAgentToolConfirmationPolicy"/>
/// entry is <see langword="true"/> (Fase 11, Checkpoint 4 —
/// <c>RequestEarlyCheckIn</c>/<c>RequestLateCheckout</c>). Separates two
/// distinct moments: proposing an action (validate the model's raw
/// arguments, narrow them to the exact minimal JSON payload persisted on
/// <c>AgentPendingAction.SanitizedArguments</c> — schema owned by this
/// method, never a generic dump of the model's own dictionary) from
/// executing it (the ordinary <see cref="IAgentTool.ExecuteAsync"/>, called
/// only after confirmation, with the arguments deserialized straight back
/// out of the very same JSON this method produced — one execution
/// entrypoint, never a second).
/// </summary>
public interface IConfirmableAgentTool : IAgentTool
{
    AgentPendingActionProposalResult BuildSanitizedArguments(IReadOnlyDictionary<string, string>? arguments);
}
