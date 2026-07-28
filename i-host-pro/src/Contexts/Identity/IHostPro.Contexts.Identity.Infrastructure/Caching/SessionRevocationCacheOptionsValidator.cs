using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.Contexts.Identity.Infrastructure.Caching;

/// <summary>
/// Fails IHostPro.Api's startup immediately (via <c>ValidateOnStart</c>) when
/// <see cref="SessionRevocationCacheOptions.ConnectionString"/> is missing or
/// syntactically malformed — never lazily on the first logout. Only the
/// syntax is checked (<see cref="ConfigurationOptions.Parse(string)"/> does
/// not connect); actual Redis reachability is deliberately never required
/// for startup to succeed (Incremento 2 plan, Etapa 12: Redis is never the
/// source of truth, so the API must still start even if Redis is down).
/// </summary>
public sealed class SessionRevocationCacheOptionsValidator : IValidateOptions<SessionRevocationCacheOptions>
{
    private static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(10);

    public ValidateOptionsResult Validate(string? name, SessionRevocationCacheOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add($"{SessionRevocationCacheOptions.SectionName}:{nameof(SessionRevocationCacheOptions.ConnectionString)} is required.");
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
                    $"{SessionRevocationCacheOptions.SectionName}:{nameof(SessionRevocationCacheOptions.ConnectionString)} is not a valid Redis connection string: {ex.Message}");
            }
        }

        if (options.ConnectTimeout < MinTimeout || options.ConnectTimeout > MaxTimeout)
        {
            failures.Add(
                $"{SessionRevocationCacheOptions.SectionName}:{nameof(SessionRevocationCacheOptions.ConnectTimeout)} must be between " +
                $"{MinTimeout} and {MaxTimeout} (was {options.ConnectTimeout}).");
        }

        if (options.OperationTimeout < MinTimeout || options.OperationTimeout > MaxTimeout)
        {
            failures.Add(
                $"{SessionRevocationCacheOptions.SectionName}:{nameof(SessionRevocationCacheOptions.OperationTimeout)} must be between " +
                $"{MinTimeout} and {MaxTimeout} (was {options.OperationTimeout}).");
        }

        if (options.ConnectRetry < 0 || options.ConnectRetry > 5)
        {
            failures.Add(
                $"{SessionRevocationCacheOptions.SectionName}:{nameof(SessionRevocationCacheOptions.ConnectRetry)} must be between 0 and 5 " +
                $"(was {options.ConnectRetry}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
