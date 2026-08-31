using IHostPro.Contexts.AIAgent.Application.Tools;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Minimal request contract (mandate item 14, CP2; evolved by CP3's own
/// mandate item 9). <see cref="AvailableTools"/> is the provider-neutral Read
/// Tool catalogue (Name + Description only, never a provider-specific JSON
/// schema) — <see langword="null"/>/empty means no tools are offered this
/// call. No tenant/model options or correlation metadata beyond what the
/// caller already carries in its own scope.
///
/// <see cref="SystemPrompt"/> is deliberately nullable and never populated
/// with hardcoded business content in this checkpoint (mandate item 18 —
/// Documento 16 §22 forbids a fixed prompt in code; the real runtime prompt
/// source is Configuration/Context Builder's future responsibility, not yet
/// wired). The pipeline passes <see langword="null"/> here — the
/// deterministic <c>FakeModelProvider</c> does not need one to function.
/// </summary>
public sealed record ModelRequest(
    string? SystemPrompt,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<AgentToolDescriptor>? AvailableTools = null);

/// <summary>A single provider-neutral turn in <see cref="ModelRequest.Messages"/> — never Communication's own <c>Message</c> aggregate, never a raw history-reader projection (mandate item 9's own "não retornar Message aggregate").</summary>
public sealed record ModelMessage(ModelMessageRole Role, string Content);

/// <summary><see cref="Tool"/> (Fase 11, Checkpoint 3) exists only for the ephemeral in-memory model loop's own sanitized tool-result turn — never persisted as a Communication <c>Message</c>.</summary>
public enum ModelMessageRole
{
    Guest,
    Agent,
    Tool,
}
