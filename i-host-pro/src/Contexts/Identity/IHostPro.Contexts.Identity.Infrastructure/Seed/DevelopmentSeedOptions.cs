namespace IHostPro.Contexts.Identity.Infrastructure.Seed;

/// <summary>
/// Development-only tenant/user bootstrap data (Incremento 2 plan, ajuste 3-4).
/// Bound and validated exclusively when the host environment is Development —
/// see the <c>isDevelopmentEnvironment</c> parameter of
/// <c>IdentityModuleExtensions.AddIdentityModule</c>. Outside Development this
/// type is never bound and never validated, regardless of what a
/// configuration source might contain: the registration itself does not
/// happen, not merely a runtime no-op.
///
/// <see cref="AdminPassword"/> must never be sourced from a committed file
/// (appsettings.json / appsettings.Development.json) — only from an
/// environment variable or User Secrets. Nothing in this module logs or
/// echoes its value.
/// </summary>
public sealed class DevelopmentSeedOptions
{
    public const string SectionName = "Identity:DevelopmentSeed";

    public bool Enabled { get; set; }
    public string TenantSlug { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string AdminEmail { get; set; } = string.Empty;
    public string AdminFullName { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
}
