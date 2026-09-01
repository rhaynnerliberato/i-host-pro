namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Thrown by any <see cref="IModelProvider"/> implementation on a
/// controlled/expected failure (mandate item 35) — never for programmer
/// errors (invalid arguments still throw ordinary exceptions).
///
/// <see cref="IsPermanent"/> (Fase 11, Checkpoint 7, mandate item 44/47):
/// <see langword="false"/> by default (every pre-CP7 throw site — the
/// deterministic <c>FakeModelProvider</c> markers — is unaffected, still
/// retried exactly once per Checkpoint 5's own policy). A real provider sets
/// it <see langword="true"/> for a failure a retry cannot possibly fix
/// (invalid/missing API key, a malformed request the API rejects, an
/// unsupported/retired model id) — <see cref="ConversationMessageReceivedProcessor.GenerateWithRetryAsync"/>
/// skips its own single retry for these, never wasting a second attempt on a
/// deterministically-repeating failure, while still reaching the exact same
/// safe-fallback-response path every other <see cref="ModelProviderException"/>
/// already does. This is a targeted extension of Checkpoint 5's existing
/// policy, never a new retry framework (mandate item 45 — no Polly, no new
/// abstraction).
/// </summary>
public sealed class ModelProviderException : Exception
{
    public bool IsPermanent { get; }

    public ModelProviderException(string message, bool isPermanent = false) : base(message)
    {
        IsPermanent = isPermanent;
    }
}
