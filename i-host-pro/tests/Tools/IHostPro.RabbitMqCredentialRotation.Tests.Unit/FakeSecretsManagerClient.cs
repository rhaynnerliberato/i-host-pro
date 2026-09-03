namespace IHostPro.RabbitMqCredentialRotation.Tests.Unit;

internal sealed class FakeSecretsManagerClient(string initialSecretString) : ISecretsManagerClient
{
    public int PutCallCount { get; private set; }
    public List<string> PutValues { get; } = [];
    public Exception? ThrowOnPut { get; set; }

    public Task<string> GetSecretStringAsync(string secretId, CancellationToken cancellationToken) =>
        Task.FromResult(initialSecretString);

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
