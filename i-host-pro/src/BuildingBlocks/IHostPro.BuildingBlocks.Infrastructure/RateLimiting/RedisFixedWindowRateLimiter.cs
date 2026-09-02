using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.BuildingBlocks.Infrastructure.RateLimiting;

/// <summary>
/// Fixed-window counter, one Redis key per (policy, partition), atomic via a
/// single Lua script (<see cref="IncrementScript"/>) — INCR and PEXPIRE never
/// run as two separate round-trips, which would otherwise race (a counter
/// could be incremented and read by a concurrent caller before its expiry is
/// set, effectively making that window immortal). The script sets the expiry
/// only on the FIRST increment of a new window, which is what makes a single
/// key self-expiring instead of needing a separate window-bucket suffix.
///
/// Every Redis operation is wrapped in a broad-but-deliberate catch, exactly
/// like <c>RedisPolicyValueCache</c> — but unlike that cache, the DEGRADATION
/// TARGET is per-policy, not a single platform-wide choice: see
/// <see cref="RateLimitFailureMode"/>.
/// </summary>
public sealed class RedisFixedWindowRateLimiter : IDistributedRateLimiter
{
    private const string IncrementScript = """
        local current = redis.call('INCR', KEYS[1])
        if tonumber(current) == 1 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return current
        """;

    private static readonly Meter Meter = new("IHostPro.RateLimiting");
    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>("rate_limit.decisions");

    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly IOptions<RateLimitingOptions> _options;
    private readonly ILogger<RedisFixedWindowRateLimiter> _logger;

    public RedisFixedWindowRateLimiter(
        IConnectionMultiplexer connectionMultiplexer, IOptions<RateLimitingOptions> options, ILogger<RedisFixedWindowRateLimiter> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _options = options;
        _logger = logger;
    }

    public async Task<RateLimitDecision> CheckAsync(string policyName, string partitionKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Value.Policies.TryGetValue(policyName, out var policy))
        {
            // A policy that was never configured is unlimited — callers only
            // reach here for policies they deliberately wired up.
            return RateLimitDecision.Allow();
        }

        if (!_options.Value.Enabled)
        {
            Decisions.Add(1, new KeyValuePair<string, object?>("policy", policyName), new KeyValuePair<string, object?>("outcome", "disabled"));
            return RateLimitDecision.Allow();
        }

        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            var key = BuildKey(policyName, partitionKey);
            var windowMs = (long)policy.Window.TotalMilliseconds;

            var result = (long)await database.ScriptEvaluateAsync(IncrementScript, [key], [windowMs]);

            if (result <= policy.PermitLimit)
            {
                Decisions.Add(1, new KeyValuePair<string, object?>("policy", policyName), new KeyValuePair<string, object?>("outcome", "allowed"));
                return RateLimitDecision.Allow();
            }

            var ttl = await database.KeyTimeToLiveAsync(key) ?? policy.Window;
            Decisions.Add(1, new KeyValuePair<string, object?>("policy", policyName), new KeyValuePair<string, object?>("outcome", "rejected"));
            return RateLimitDecision.Deny(ttl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failOpen = policy.FailureMode == RateLimitFailureMode.FailOpen;
            _logger.LogWarning(
                ex,
                "Rate limit check failed for policy {PolicyName} — degrading to {FailureMode}.",
                policyName, policy.FailureMode);
            Decisions.Add(1,
                new KeyValuePair<string, object?>("policy", policyName),
                new KeyValuePair<string, object?>("outcome", failOpen ? "fail_open" : "fail_closed"));

            return failOpen
                ? new RateLimitDecision(true, FailedOpen: true)
                : RateLimitDecision.Deny(policy.Window);
        }
    }

    private static RedisKey BuildKey(string policyName, string partitionKey) => $"ihostpro:ratelimit:{policyName}:{partitionKey}";
}
