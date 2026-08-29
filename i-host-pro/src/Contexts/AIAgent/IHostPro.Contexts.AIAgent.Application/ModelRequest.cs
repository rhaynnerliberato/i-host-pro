namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Minimal request contract (mandate item 14) — deliberately limited to
/// <see cref="SystemPrompt"/> + <see cref="Messages"/>, the mandate's own
/// stated floor ("pode limitar a: ModelRequest(SystemPrompt, Messages) ou
/// equivalente mínimo"). No <c>AvailableTools</c> metadata (CP2 implements
/// zero Tools, mandate item 21/14 — "talvez NÃO ainda"), no tenant/model
/// options or correlation metadata beyond what the caller already carries in
/// its own scope.
///
/// <see cref="SystemPrompt"/> is deliberately nullable and never populated
/// with hardcoded business content in this checkpoint (mandate item 18 —
/// Documento 16 §22 forbids a fixed prompt in code; the real runtime prompt
/// source is Configuration/Context Builder's future responsibility, not yet
/// wired). CP2's own pipeline passes <see langword="null"/> here — the
/// deterministic <c>FakeModelProvider</c> does not need one to function.
/// </summary>
public sealed record ModelRequest(string? SystemPrompt, IReadOnlyList<ModelMessage> Messages);

/// <summary>A single provider-neutral turn in <see cref="ModelRequest.Messages"/> — never Communication's own <c>Message</c> aggregate, never a raw history-reader projection (mandate item 9's own "não retornar Message aggregate").</summary>
public sealed record ModelMessage(ModelMessageRole Role, string Content);

public enum ModelMessageRole
{
    Guest,
    Agent,
}
