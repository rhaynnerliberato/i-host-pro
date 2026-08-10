namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// Thrown by every typed policy reader when the engine cannot answer at all
/// — timeout, database/cache failure, unexpected error, engine down (Fase 5,
/// Incremento 1 official decision 4). Never thrown, and never caught and
/// converted, to mean "no value is configured" — that case is
/// <see cref="PolicyReadStatus.NotConfigured"/>, a completely different,
/// successful outcome. Consumers must let this propagate and fail their own
/// operation — never substitute a hardcoded or optimistic value, and never
/// treat this as equivalent to <see cref="PolicyReadStatus.NotConfigured"/>.
/// </summary>
public sealed class PolicyEngineUnavailableException : Exception
{
    public PolicyEngineUnavailableException(string message) : base(message)
    {
    }

    public PolicyEngineUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
