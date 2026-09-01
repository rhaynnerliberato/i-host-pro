using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;

/// <summary>
/// Every DTO in this file is Infrastructure-private (Fase 11, Checkpoint 7,
/// mandate item 6) — none of these types is ever referenced from
/// AIAgent.Domain, AIAgent.Application's public contracts
/// (<see cref="Application.ModelRequest"/>/<see cref="Application.ModelResult"/>),
/// or any Tool contract. <see cref="AnthropicModelProvider"/> is the only
/// class that ever constructs or deserializes these.
/// </summary>
internal sealed class AnthropicRequestBody
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; init; }

    [JsonPropertyName("system")]
    public string? System { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<AnthropicRequestMessage> Messages { get; init; }

    [JsonPropertyName("tools")]
    public required IReadOnlyList<AnthropicToolDefinition> Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public required AnthropicToolChoice ToolChoice { get; init; }
}

internal sealed class AnthropicRequestMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required IReadOnlyList<AnthropicRequestContentBlock> Content { get; init; }
}

/// <summary>
/// Requests in this provider only ever send <c>"text"</c> blocks — never a
/// replayed <c>tool_use</c>/<c>tool_result</c> block. <see cref="Application.ModelMessage"/>'s
/// own neutral history (Guest/Agent/Tool roles, plain text only, no tool-call
/// id) does not carry what real Anthropic tool_use/tool_result linkage
/// requires; reconstructing it would need state this stateless, per-call
/// provider does not have. A <see cref="Application.ModelMessageRole.Tool"/>
/// turn is instead sent as an ordinary user-role text block, clearly
/// prefixed so the model never mistakes it for the guest's own words — see
/// <see cref="AnthropicModelProvider.ToAnthropicMessages"/>.
/// </summary>
internal sealed class AnthropicRequestContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

internal sealed class AnthropicToolDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("input_schema")]
    public required JsonObject InputSchema { get; init; }
}

internal sealed class AnthropicToolChoice
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("disable_parallel_tool_use")]
    public bool DisableParallelToolUse { get; init; } = true;

    public static AnthropicToolChoice Any() => new() { Type = "any" };

    public static AnthropicToolChoice ForceTool(string name) => new() { Type = "tool", Name = name };
}

internal sealed class AnthropicResponseBody
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("content")]
    public IReadOnlyList<AnthropicResponseContentBlock>? Content { get; init; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; init; }
}

internal sealed class AnthropicResponseContentBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("input")]
    public JsonObject? Input { get; init; }
}

internal sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}

/// <summary>Anthropic's own error envelope (<c>{"type":"error","error":{"type":"...","message":"..."}}</c>) — <see cref="Message"/> is never logged (mandate item 40/47 — no raw response body/content ever logged), only <see cref="Type"/> (a short, stable, non-sensitive code).</summary>
internal sealed class AnthropicErrorResponse
{
    [JsonPropertyName("error")]
    public AnthropicErrorDetail? Error { get; init; }
}

internal sealed class AnthropicErrorDetail
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
