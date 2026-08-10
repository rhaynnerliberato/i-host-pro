using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.Contexts.Configuration.Infrastructure.Caching;

/// <summary>
/// Fails the hosting process's startup immediately (via <c>ValidateOnStart</c>)
/// when <see cref="PolicyCacheOptions.ConnectionString"/> is missing or
/// syntactically malformed — mirrors <c>SessionRevocationCacheOptionsValidator</c>
/// exactly. Only the syntax is checked; actual Redis reachability is
/// deliberately never required for startup to succeed — a cache read/write
/// failure degrades to PostgreSQL (see <see cref="RedisPolicyValueCache"/>'s
/// own doc comment), so the host must still start even if Redis is down.
/// </summary>
public sealed class PolicyCacheOptionsValidator : IValidateOptions<PolicyCacheOptions>
{
    private static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinTimeToLive = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxTimeToLive = TimeSpan.FromHours(1);

    public ValidateOptionsResult Validate(string? name, PolicyCacheOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add($"{PolicyCacheOptions.SectionName}:{nameof(PolicyCacheOptions.ConnectionString)} is required.");
        }
        else
        {
            try
            {
                ConfigurationOptions.Parse(options.ConnectionString);
            }
            catch (Exception ex)
            {
                failures.Add(
                    $"{PolicyCacheOptions.SectionName}:{nameof(PolicyCacheOptions.ConnectionString)} is not a valid Redis connection string: {ex.Message}");
            }
        }

        if (options.ConnectTimeout < MinTimeout || options.ConnectTimeout > MaxTimeout)
        {
            failures.Add(
                $"{PolicyCacheOptions.SectionName}:{nameof(PolicyCacheOptions.ConnectTimeout)} must be between " +
                $"{MinTimeout} and {MaxTimeout} (was {options.ConnectTimeout}).");
        }

        if (options.OperationTimeout < MinTimeout || options.OperationTimeout > MaxTimeout)
        {
            failures.Add(
                $"{PolicyCacheOptions.SectionName}:{nameof(PolicyCacheOptions.OperationTimeout)} must be between " +
                $"{MinTimeout} and {MaxTimeout} (was {options.OperationTimeout}).");
        }

        if (options.ConnectRetry < 0 || options.ConnectRetry > 5)
        {
            failures.Add(
                $"{PolicyCacheOptions.SectionName}:{nameof(PolicyCacheOptions.ConnectRetry)} must be between 0 and 5 " +
                $"(was {options.ConnectRetry}).");
        }

        if (options.TimeToLive < MinTimeToLive || options.TimeToLive > MaxTimeToLive)
        {
            failures.Add(
                $"{PolicyCacheOptions.SectionName}:{nameof(PolicyCacheOptions.TimeToLive)} must be between " +
                $"{MinTimeToLive} and {MaxTimeToLive} (was {options.TimeToLive}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
