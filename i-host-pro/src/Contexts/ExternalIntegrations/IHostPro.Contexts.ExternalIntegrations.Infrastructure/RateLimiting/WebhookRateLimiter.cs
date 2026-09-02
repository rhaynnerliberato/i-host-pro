using IHostPro.BuildingBlocks.Infrastructure.RateLimiting;
using IHostPro.Contexts.ExternalIntegrations.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.RateLimiting;

/// <inheritdoc cref="IWebhookRateLimiter"/>
public sealed class WebhookRateLimiter : IWebhookRateLimiter
{
    private const string PolicyName = "Webhook";

    private readonly IDistributedRateLimiter _limiter;

    public WebhookRateLimiter(IDistributedRateLimiter limiter) => _limiter = limiter;

    public async Task<WebhookRateLimitDecision> CheckAsync(string partitionKey, CancellationToken cancellationToken)
    {
        var decision = await _limiter.CheckAsync(PolicyName, partitionKey, cancellationToken);
        return new WebhookRateLimitDecision(decision.Allowed, decision.RetryAfter);
    }
}
