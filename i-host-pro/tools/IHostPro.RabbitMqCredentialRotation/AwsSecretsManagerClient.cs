using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace IHostPro.RabbitMqCredentialRotation;

public sealed class AwsSecretsManagerClient(IAmazonSecretsManager client) : ISecretsManagerClient
{
    public async Task<string> GetSecretStringAsync(string secretId, CancellationToken cancellationToken)
    {
        var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, cancellationToken);
        return response.SecretString
            ?? throw new InvalidOperationException($"Secret {secretId} has no SecretString value.");
    }

    public Task PutSecretStringAsync(string secretId, string secretString, CancellationToken cancellationToken) =>
        client.PutSecretValueAsync(new PutSecretValueRequest { SecretId = secretId, SecretString = secretString }, cancellationToken);
}
