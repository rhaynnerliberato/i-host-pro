using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.BuildingBlocks.Infrastructure.RateLimiting;

/// <summary>
/// Fails the hosting process's startup immediately (via <c>ValidateOnStart</c>)
/// when the configuration is malformed — mirrors <c>PolicyCacheOptionsValidator</c>
/// exactly. Only syntax/range is checked; actual Redis reachability is
/// deliberately never required for startup, since every policy has an
/// explicit degradation behavior for when Redis is down (<see cref="RateLimitFailureMode"/>).
/// </summary>
public sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    private static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxWindow = TimeSpan.FromHours(1);

    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        var failures = new List<string>();

        // Required regardless of Enabled: Enabled is an operational
        // kill-switch checked per-call (CheckAsync short-circuits to Allow
        // without touching Redis), never a reason to skip provisioning the
        // connection itself — toggling it back on at runtime (config reload)
        // must not require a process restart to reconnect.
        if (string.IsNullOrWhiteSpace(options.Redis.ConnectionString))
        {
            failures.Add($"{RateLimitingOptions.SectionName}:Redis:ConnectionString is required.");
        }
        else
        {
            try
            {
                ConfigurationOptions.Parse(options.Redis.ConnectionString);
            }
            catch (Exception ex)
            {
                failures.Add($"{RateLimitingOptions.SectionName}:Redis:ConnectionString is not a valid Redis connection string: {ex.Message}");
            }
        }

        if (options.Redis.ConnectTimeout < MinTimeout || options.Redis.ConnectTimeout > MaxTimeout)
            failures.Add($"{RateLimitingOptions.SectionName}:Redis:ConnectTimeout must be between {MinTimeout} and {MaxTimeout} (was {options.Redis.ConnectTimeout}).");

        if (options.Redis.OperationTimeout < MinTimeout || options.Redis.OperationTimeout > MaxTimeout)
            failures.Add($"{RateLimitingOptions.SectionName}:Redis:OperationTimeout must be between {MinTimeout} and {MaxTimeout} (was {options.Redis.OperationTimeout}).");

        if (options.Redis.ConnectRetry < 0 || options.Redis.ConnectRetry > 5)
            failures.Add($"{RateLimitingOptions.SectionName}:Redis:ConnectRetry must be between 0 and 5 (was {options.Redis.ConnectRetry}).");

        foreach (var (policyName, policy) in options.Policies)
        {
            if (policy.PermitLimit < 1)
                failures.Add($"{RateLimitingOptions.SectionName}:Policies:{policyName}:PermitLimit must be at least 1 (was {policy.PermitLimit}).");

            if (policy.Window < MinWindow || policy.Window > MaxWindow)
                failures.Add($"{RateLimitingOptions.SectionName}:Policies:{policyName}:Window must be between {MinWindow} and {MaxWindow} (was {policy.Window}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
