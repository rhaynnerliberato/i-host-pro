namespace IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;

/// <summary>
/// Minimal abstraction over AWS Secrets Manager's GetSecretValue operation —
/// deliberately NOT the full <c>IAmazonSecretsManager</c> surface (dozens of
/// unrelated operations), so <see cref="SecretsManagerAnthropicCredentialProvider"/>
/// can be tested with a small hand-rolled fake instead of a mocking library
/// (none is used anywhere in this codebase). Local to this Infrastructure
/// project — mirrors <c>MetaHttpCircuitBreakerOptions</c>'s own
/// "deliberately NOT shared between Infrastructure projects" precedent.
/// </summary>
public interface ISecretValueReader
{
    /// <summary>
    /// Returns the secret's string value, or throws if it cannot be
    /// resolved (not found, access denied, throttled, transient error) —
    /// the caller decides how to turn that into a fail-closed result; this
    /// abstraction never swallows an error into a null itself.
    /// </summary>
    Task<string> GetSecretStringAsync(string secretId, CancellationToken cancellationToken);
}
