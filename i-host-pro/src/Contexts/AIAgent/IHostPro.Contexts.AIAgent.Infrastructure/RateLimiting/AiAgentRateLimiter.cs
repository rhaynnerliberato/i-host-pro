using IHostPro.BuildingBlocks.Infrastructure.RateLimiting;
using IHostPro.Contexts.AIAgent.Application;

namespace IHostPro.Contexts.AIAgent.Infrastructure.RateLimiting;

/// <inheritdoc cref="IAiAgentRateLimiter"/>
public sealed class AiAgentRateLimiter : IAiAgentRateLimiter
{
    private const string PolicyName = "AiExpensiveOperation";

    private readonly IDistributedRateLimiter _limiter;

    public AiAgentRateLimiter(IDistributedRateLimiter limiter) => _limiter = limiter;

    public async Task<bool> IsAllowedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var decision = await _limiter.CheckAsync(PolicyName, tenantId.ToString("N"), cancellationToken);
        return decision.Allowed;
    }
}
