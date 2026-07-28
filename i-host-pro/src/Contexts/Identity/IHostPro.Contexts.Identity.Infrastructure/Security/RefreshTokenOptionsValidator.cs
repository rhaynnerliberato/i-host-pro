using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

public sealed class RefreshTokenOptionsValidator : IValidateOptions<RefreshTokenOptions>
{
    private static readonly TimeSpan MinLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(90);
    private const int MinSecretSizeBytes = 16;
    private const int MaxSecretSizeBytes = 64;
    private static readonly TimeSpan MinGraceWindow = TimeSpan.Zero;
    private static readonly TimeSpan MaxGraceWindow = TimeSpan.FromSeconds(60);

    public ValidateOptionsResult Validate(string? name, RefreshTokenOptions options)
    {
        var failures = new List<string>();

        if (options.Lifetime < MinLifetime || options.Lifetime > MaxLifetime)
        {
            failures.Add(
                $"{RefreshTokenOptions.SectionName}:{nameof(RefreshTokenOptions.Lifetime)} must be between " +
                $"{MinLifetime} and {MaxLifetime} (was {options.Lifetime}).");
        }

        if (options.SecretSizeBytes < MinSecretSizeBytes || options.SecretSizeBytes > MaxSecretSizeBytes)
        {
            failures.Add(
                $"{RefreshTokenOptions.SectionName}:{nameof(RefreshTokenOptions.SecretSizeBytes)} must be between " +
                $"{MinSecretSizeBytes} and {MaxSecretSizeBytes} bytes (was {options.SecretSizeBytes}).");
        }

        if (options.ConcurrentRotationGraceWindow < MinGraceWindow || options.ConcurrentRotationGraceWindow > MaxGraceWindow)
        {
            failures.Add(
                $"{RefreshTokenOptions.SectionName}:{nameof(RefreshTokenOptions.ConcurrentRotationGraceWindow)} must be between " +
                $"{MinGraceWindow} and {MaxGraceWindow} (was {options.ConcurrentRotationGraceWindow}) — a larger window " +
                "would materially weaken reuse detection.");
        }

        // Defense-in-depth: unreachable with the current bounds above (max
        // grace window is always far smaller than min lifetime), but kept in
        // case either bound is ever relaxed independently of the other.
        if (failures.Count == 0 && options.ConcurrentRotationGraceWindow >= options.Lifetime)
        {
            failures.Add(
                $"{RefreshTokenOptions.SectionName}:{nameof(RefreshTokenOptions.ConcurrentRotationGraceWindow)} must be " +
                $"smaller than {RefreshTokenOptions.SectionName}:{nameof(RefreshTokenOptions.Lifetime)}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
