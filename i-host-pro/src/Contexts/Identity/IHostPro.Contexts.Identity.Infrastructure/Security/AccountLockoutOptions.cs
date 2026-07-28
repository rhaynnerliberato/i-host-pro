namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Effective lockout policy applied to ASP.NET Core Identity's
/// <c>IdentityOptions.Lockout</c> (Incremento 2 plan, Etapa 9) — replaces the
/// framework defaults that were silently in effect until now (Increment 1
/// only ever set <c>AllowedForNewUsers</c>, never
/// <c>MaxFailedAccessAttempts</c>/<c>DefaultLockoutTimeSpan</c> explicitly).
/// Read by <c>IdentityModuleExtensions</c> and applied via
/// <c>OptionsBuilder&lt;IdentityOptions&gt;.PostConfigure</c> — the handler
/// never reads these values directly nor hardcodes a threshold/duration; it
/// only calls <c>UserManager&lt;User&gt;</c> methods that consult
/// <c>IdentityOptions.Lockout</c> internally.
///
/// Defaults match ASP.NET Core Identity's own well-known out-of-box values —
/// a recognized, defensible baseline, not an arbitrary invention — and
/// remain adjustable per environment via configuration.
/// </summary>
public sealed class AccountLockoutOptions
{
    public const string SectionName = "Identity:AccountLockout";

    public int MaxFailedAccessAttempts { get; set; } = 5;
    public TimeSpan DefaultLockoutDuration { get; set; } = TimeSpan.FromMinutes(5);
    public bool AllowedForNewUsers { get; set; } = true;
}
