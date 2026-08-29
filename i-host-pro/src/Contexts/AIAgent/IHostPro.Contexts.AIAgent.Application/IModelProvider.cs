namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Provider-neutral model abstraction (mandate item 13, ADR-009). Never
/// leaks an Anthropic (or any other real provider) request/response DTO to
/// Domain/Application — the sole implementation this checkpoint,
/// <c>FakeModelProvider</c> (Infrastructure), is deterministic and makes
/// zero network calls; a real Anthropic adapter is Checkpoint 7's scope.
/// </summary>
public interface IModelProvider
{
    /// <summary>Provider-neutral identity, known before any call — lets the caller record which provider/model was ATTEMPTED even when <see cref="GenerateAsync"/> throws before producing a <see cref="ModelResult"/>.</summary>
    string ProviderName { get; }

    string ModelName { get; }

    Task<ModelResult> GenerateAsync(ModelRequest request, CancellationToken cancellationToken);
}
