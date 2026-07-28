namespace IHostPro.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Synchronous validator for <see cref="RabbitMqClientTimeoutOptions"/> — see
/// that class's doc comment for why this cannot be a normal
/// <c>IValidateOptions&lt;T&gt;</c> resolved through <c>ValidateOnStart()</c>.
/// Bounds: long enough to tolerate a momentarily slow (but reachable) broker
/// without a false-positive "unavailable" classification, short enough that
/// an unreachable broker cannot meaningfully block an HTTP request (Incremento
/// 2 plan, homologação real).
/// </summary>
public static class RabbitMqClientTimeoutOptionsValidator
{
    private static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(30);

    public static void ValidateAndThrow(RabbitMqClientTimeoutOptions options)
    {
        if (options.ConnectTimeout < MinTimeout || options.ConnectTimeout > MaxTimeout)
        {
            throw new InvalidOperationException(
                $"{RabbitMqClientTimeoutOptions.SectionName}:{nameof(RabbitMqClientTimeoutOptions.ConnectTimeout)} " +
                $"must be between {MinTimeout} and {MaxTimeout} (was {options.ConnectTimeout}).");
        }

        if (options.ContinuationTimeout < MinTimeout || options.ContinuationTimeout > MaxTimeout)
        {
            throw new InvalidOperationException(
                $"{RabbitMqClientTimeoutOptions.SectionName}:{nameof(RabbitMqClientTimeoutOptions.ContinuationTimeout)} " +
                $"must be between {MinTimeout} and {MaxTimeout} (was {options.ContinuationTimeout}).");
        }
    }
}
