namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>Thrown by any <see cref="IModelProvider"/> implementation on a controlled/expected failure (mandate item 35) — never for programmer errors (invalid arguments still throw ordinary exceptions).</summary>
public sealed class ModelProviderException : Exception
{
    public ModelProviderException(string message) : base(message)
    {
    }
}
