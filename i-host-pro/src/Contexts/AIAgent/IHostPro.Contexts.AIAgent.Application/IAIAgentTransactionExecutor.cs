namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>Mirrors <c>ICommunicationTransactionExecutor</c> exactly — a real Postgres transaction/RLS scope wrapper in Infrastructure, a pass-through in unit tests.</summary>
public interface IAIAgentTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
