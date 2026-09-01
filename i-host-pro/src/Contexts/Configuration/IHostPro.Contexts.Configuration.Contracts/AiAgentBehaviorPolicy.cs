namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The typed, deserialized shape of the <c>AI_AGENT_BEHAVIOR</c> policy (Fase
/// 11, Checkpoint 7 — mandate item 12/13). Deliberately minimal: no
/// <c>Temperature</c> (the selected Anthropic model, <c>claude-sonnet-4-6</c>,
/// rejects any custom value — see the CP7 homologation's own governance
/// record), and no additional knobs (<c>EmojiMode</c>/<c>MaxSentenceCount</c>/
/// <c>VerbosityLevel</c>/<c>PersonaId</c>/<c>PromptVersion</c>) beyond what the
/// mandate explicitly approved. This type is only ever produced by
/// <see cref="IAiAgentBehaviorPolicyReader"/> when
/// <see cref="PolicyReadStatus.Resolved"/>.
/// </summary>
public sealed record AiAgentBehaviorPolicy(
    string SystemPrompt,
    string? Tone,
    string? Formality);
