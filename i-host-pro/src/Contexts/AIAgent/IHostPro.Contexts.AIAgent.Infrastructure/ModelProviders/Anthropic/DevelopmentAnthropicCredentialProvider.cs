using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;

/// <summary>
/// Development-only <see cref="IAnthropicCredentialProvider"/> — resolves
/// <c>AIAgent:Anthropic:Secrets:ApiKey</c> from <see cref="IConfiguration"/>
/// (User Secrets/environment variables in Development, never a committed
/// <c>appsettings.json</c> value), mirroring <c>DevelopmentWhatsAppCredentialProvider</c>'s
/// own precedent exactly (Fase 11, Checkpoint 7, mandate item 7/8/10).
/// Registered only when <c>IsDevelopment()</c> (see <c>AIAgentModuleExtensions</c>)
/// — never registered for any other environment, so resolving
/// <see cref="IAnthropicCredentialProvider"/> outside Development fails
/// loudly (no implementation registered) instead of silently falling back to
/// this one. <c>ProductionAnthropicSecretBackend=false</c> — no real secret
/// store (Key Vault or equivalent) exists yet for any provider in this
/// codebase; building one is explicitly out of this checkpoint's scope.
///
/// Never logs the resolved value.
/// </summary>
public sealed class DevelopmentAnthropicCredentialProvider : IAnthropicCredentialProvider
{
    private const string ConfigurationKey = "AIAgent:Anthropic:Secrets:ApiKey";

    private readonly IConfiguration _configuration;

    public DevelopmentAnthropicCredentialProvider(IConfiguration configuration) => _configuration = configuration;

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_configuration[ConfigurationKey]);
}
