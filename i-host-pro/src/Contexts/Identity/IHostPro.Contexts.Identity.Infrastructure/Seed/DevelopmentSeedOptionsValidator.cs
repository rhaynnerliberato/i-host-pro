using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Seed;

/// <summary>
/// Only registered/executed when the host environment is Development (see
/// <c>IdentityModuleExtensions.AddIdentityModule</c>). When
/// <see cref="DevelopmentSeedOptions.Enabled"/> is <see langword="false"/>
/// (the default), validation always succeeds without inspecting the other
/// fields — an unconfigured/disabled seed must never block startup.
///
/// When <see cref="DevelopmentSeedOptions.Enabled"/> is <see langword="true"/>,
/// every field becomes required. <see cref="DevelopmentSeedOptions.AdminPassword"/>
/// is checked only for presence — its value is never included in a failure
/// message, and its complexity is intentionally not re-validated here: that
/// is already enforced by the existing <c>PasswordPolicyValidator</c> at the
/// point where the seeder actually creates the user, avoiding a second,
/// divergent source of truth for password policy.
/// </summary>
public sealed class DevelopmentSeedOptionsValidator : IValidateOptions<DevelopmentSeedOptions>
{
    private const int MinTenantSlugLength = 3;
    private const int MaxTenantSlugLength = 63;
    private const int MaxTenantNameLength = 200;
    private const int MaxEmailLength = 320;
    private const int MaxFullNameLength = 200;

    public ValidateOptionsResult Validate(string? name, DevelopmentSeedOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.TenantSlug))
        {
            failures.Add(RequiredWhenEnabled(nameof(DevelopmentSeedOptions.TenantSlug)));
        }
        else if (options.TenantSlug.Length is < MinTenantSlugLength or > MaxTenantSlugLength)
        {
            failures.Add(
                $"{DevelopmentSeedOptions.SectionName}:{nameof(DevelopmentSeedOptions.TenantSlug)} must be between " +
                $"{MinTenantSlugLength} and {MaxTenantSlugLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(options.TenantName))
            failures.Add(RequiredWhenEnabled(nameof(DevelopmentSeedOptions.TenantName)));
        else if (options.TenantName.Length > MaxTenantNameLength)
            failures.Add(MaxLength(nameof(DevelopmentSeedOptions.TenantName), MaxTenantNameLength));

        if (string.IsNullOrWhiteSpace(options.AdminEmail))
            failures.Add(RequiredWhenEnabled(nameof(DevelopmentSeedOptions.AdminEmail)));
        else if (options.AdminEmail.Length > MaxEmailLength || !options.AdminEmail.Contains('@'))
        {
            failures.Add(
                $"{DevelopmentSeedOptions.SectionName}:{nameof(DevelopmentSeedOptions.AdminEmail)} is not a valid " +
                "e-mail address.");
        }

        if (string.IsNullOrWhiteSpace(options.AdminFullName))
            failures.Add(RequiredWhenEnabled(nameof(DevelopmentSeedOptions.AdminFullName)));
        else if (options.AdminFullName.Length > MaxFullNameLength)
            failures.Add(MaxLength(nameof(DevelopmentSeedOptions.AdminFullName), MaxFullNameLength));

        // Presence-only check — the value itself never appears in the failure
        // message, in a log, or anywhere else.
        if (string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            failures.Add(
                $"{DevelopmentSeedOptions.SectionName}:{nameof(DevelopmentSeedOptions.AdminPassword)} is required " +
                $"when {nameof(DevelopmentSeedOptions.Enabled)} is true, but no value was found. Provide it via an " +
                "environment variable (e.g. Identity__DevelopmentSeed__AdminPassword) or User Secrets — never in a " +
                "committed appsettings file.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string RequiredWhenEnabled(string property) =>
        $"{DevelopmentSeedOptions.SectionName}:{property} is required when " +
        $"{nameof(DevelopmentSeedOptions.Enabled)} is true.";

    private static string MaxLength(string property, int max) =>
        $"{DevelopmentSeedOptions.SectionName}:{property} must be at most {max} characters.";
}
