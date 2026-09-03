using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// The only real implementation of <see cref="ISecretValueReader"/> — a thin
/// pass-through to <see cref="IAmazonSecretsManager"/>, deliberately not
/// unit-tested directly (a straight SDK call, same reasoning this codebase
/// already applies to not unit-testing raw EF Core/HttpClient wiring); the
/// logic worth testing lives in the two Secrets Manager-backed credential
/// providers against a fake <see cref="ISecretValueReader"/> instead.
/// </summary>
public sealed class AwsSecretsManagerValueReader : ISecretValueReader
{
    private readonly IAmazonSecretsManager _secretsManager;

    public AwsSecretsManagerValueReader(IAmazonSecretsManager secretsManager) => _secretsManager = secretsManager;

    public async Task<string> GetSecretStringAsync(string secretId, CancellationToken cancellationToken)
    {
        var response = await _secretsManager.GetSecretValueAsync(
            new GetSecretValueRequest { SecretId = secretId }, cancellationToken);

        if (string.IsNullOrEmpty(response.SecretString))
            throw new InvalidOperationException($"AWS Secrets Manager secret '{secretId}' has no string value.");

        return response.SecretString;
    }
}
