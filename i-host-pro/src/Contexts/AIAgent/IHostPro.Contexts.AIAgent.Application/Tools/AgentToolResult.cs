namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// The outcome of a single <see cref="IAgentTool"/> execution (Fase 11,
/// Checkpoint 3). <see cref="Content"/> is the sanitized, guest-appropriate
/// text handed back to <see cref="IModelProvider"/> for its second call —
/// never a raw entity/DTO, never a credential/secret-reference/QR/payer
/// payload. <see cref="FailureCode"/> mirrors <c>AgentToolExecution.FailureCode</c>'s
/// own convention: short, sanitized, provider/tool-neutral.
/// </summary>
public sealed record AgentToolResult
{
    public bool IsSuccess { get; }
    public string? Content { get; }
    public string? FailureCode { get; }

    private AgentToolResult(bool isSuccess, string? content, string? failureCode)
    {
        IsSuccess = isSuccess;
        Content = content;
        FailureCode = failureCode;
    }

    public static AgentToolResult Success(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Content cannot be empty.", nameof(content));

        return new AgentToolResult(true, content, null);
    }

    public static AgentToolResult Failure(string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
            throw new ArgumentException("Failure code cannot be empty.", nameof(failureCode));

        return new AgentToolResult(false, null, failureCode);
    }
}
