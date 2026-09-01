namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// The outcome of <see cref="IConfirmableAgentTool.BuildSanitizedArguments"/>
/// (Fase 11, Checkpoint 4). <see cref="SanitizedArgumentsJson"/> is the
/// exact, minimal, application-controlled JSON payload to persist on
/// <c>AgentPendingAction.SanitizedArguments</c> — never a raw dump of the
/// model's own arguments dictionary. Mirrors <see cref="AgentToolResult"/>'s
/// own success/failure shape.
/// </summary>
public sealed record AgentPendingActionProposalResult
{
    public bool IsSuccess { get; }
    public string? SanitizedArgumentsJson { get; }
    public string? FailureCode { get; }

    private AgentPendingActionProposalResult(bool isSuccess, string? sanitizedArgumentsJson, string? failureCode)
    {
        IsSuccess = isSuccess;
        SanitizedArgumentsJson = sanitizedArgumentsJson;
        FailureCode = failureCode;
    }

    public static AgentPendingActionProposalResult Success(string sanitizedArgumentsJson)
    {
        if (string.IsNullOrWhiteSpace(sanitizedArgumentsJson))
            throw new ArgumentException("Sanitized arguments JSON cannot be empty.", nameof(sanitizedArgumentsJson));

        return new AgentPendingActionProposalResult(true, sanitizedArgumentsJson, null);
    }

    public static AgentPendingActionProposalResult Failure(string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
            throw new ArgumentException("Failure code cannot be empty.", nameof(failureCode));

        return new AgentPendingActionProposalResult(false, null, failureCode);
    }
}
