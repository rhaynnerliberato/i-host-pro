using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Validated eagerly at startup via <c>ValidateOnStart()</c> (registered in
/// <c>IdentityModuleExtensions</c>) — a misconfigured <see cref="JwtOptions"/>
/// fails the host before it accepts any request, never lazily on first login.
/// </summary>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    private const int MaxIssuerAudienceLength = 2048;
    private static readonly TimeSpan MinAccessTokenLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxAccessTokenLifetime = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan MinClockSkew = TimeSpan.Zero;
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
            failures.Add(Required(nameof(JwtOptions.Issuer)));
        else if (options.Issuer.Length > MaxIssuerAudienceLength)
            failures.Add(MaxLength(nameof(JwtOptions.Issuer), MaxIssuerAudienceLength));

        if (string.IsNullOrWhiteSpace(options.Audience))
            failures.Add(Required(nameof(JwtOptions.Audience)));
        else if (options.Audience.Length > MaxIssuerAudienceLength)
            failures.Add(MaxLength(nameof(JwtOptions.Audience), MaxIssuerAudienceLength));

        if (options.AccessTokenLifetime < MinAccessTokenLifetime || options.AccessTokenLifetime > MaxAccessTokenLifetime)
        {
            failures.Add(
                $"{JwtOptions.SectionName}:{nameof(JwtOptions.AccessTokenLifetime)} must be between " +
                $"{MinAccessTokenLifetime} and {MaxAccessTokenLifetime} (was {options.AccessTokenLifetime}).");
        }

        if (options.ClockSkew < MinClockSkew || options.ClockSkew > MaxClockSkew)
        {
            failures.Add(
                $"{JwtOptions.SectionName}:{nameof(JwtOptions.ClockSkew)} must be between " +
                $"{MinClockSkew} and {MaxClockSkew} (was {options.ClockSkew}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string Required(string property) => $"{JwtOptions.SectionName}:{property} is required.";

    private static string MaxLength(string property, int max) =>
        $"{JwtOptions.SectionName}:{property} must be at most {max} characters.";
}
