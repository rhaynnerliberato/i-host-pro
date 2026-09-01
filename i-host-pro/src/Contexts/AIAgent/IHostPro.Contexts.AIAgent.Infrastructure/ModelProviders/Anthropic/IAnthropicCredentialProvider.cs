namespace IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;

/// <summary>
/// Resolves the Anthropic API key — never <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// directly from <see cref="AnthropicModelProvider"/> (Fase 11, Checkpoint 7,
/// mandate item 7/8, mirroring <c>IWhatsAppCredentialProvider</c>'s own
/// precedent exactly). Never logs the resolved value.
/// </summary>
public interface IAnthropicCredentialProvider
{
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken);
}
