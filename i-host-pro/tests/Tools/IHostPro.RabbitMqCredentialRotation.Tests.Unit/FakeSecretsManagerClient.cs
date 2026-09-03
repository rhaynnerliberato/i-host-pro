namespace IHostPro.RabbitMqCredentialRotation.Tests.Unit;

internal sealed class FakeSecretsManagerClient(string initialSecretString, string? previousSecretString = null) : ISecretsManagerClient
{
    public int PutCallCount { get; private set; }
    public List<string> PutValues { get; } = [];
    public Exception? ThrowOnPut { get; set; }

    public Task<string> GetSecretStringAsync(string secretId, string versionStage, CancellationToken cancellationToken) =>
        Task.FromResult(versionStage == "AWSPREVIOUS" && previousSecretString is not null ? previousSecretString : initialSecretString);

    public Task PutSecretStringAsync(string secretId, string secretString, CancellationToken cancellationToken)
    {
        PutCallCount++;
        PutValues.Add(secretString);

        if (ThrowOnPut is not null)
        {
            throw ThrowOnPut;
        }

        return Task.CompletedTask;
    }
}
