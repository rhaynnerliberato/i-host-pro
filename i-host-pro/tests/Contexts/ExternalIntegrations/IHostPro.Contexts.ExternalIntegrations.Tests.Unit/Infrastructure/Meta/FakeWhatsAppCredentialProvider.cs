using IHostPro.Contexts.ExternalIntegrations.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.Meta;

internal sealed class FakeWhatsAppCredentialProvider(string? secretValue) : IWhatsAppCredentialProvider
{
    public static FakeWhatsAppCredentialProvider Returning(string? secretValue) => new(secretValue);

    public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken) => Task.FromResult(secretValue);
}
