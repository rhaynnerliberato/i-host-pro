using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.ModelProviders.Anthropic;

internal sealed class FakeAnthropicCredentialProvider : IAnthropicCredentialProvider
{
    private readonly string? _apiKey;

    private FakeAnthropicCredentialProvider(string? apiKey) => _apiKey = apiKey;

    public static FakeAnthropicCredentialProvider Returning(string? apiKey) => new(apiKey);

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) => Task.FromResult(_apiKey);
}
