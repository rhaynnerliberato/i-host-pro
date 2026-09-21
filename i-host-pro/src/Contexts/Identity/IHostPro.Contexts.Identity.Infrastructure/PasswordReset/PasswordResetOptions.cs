namespace IHostPro.Contexts.Identity.Infrastructure.PasswordReset;

/// <summary>
/// Non-secret config for the Self-Service Identity &amp; Onboarding
/// Foundation gate's forgot-password flow. <see cref="FrontendResetUrlBase"/>
/// is explicit trusted configuration — the reset link is never built from
/// the request's Host/Origin/Referer header (Architecture + Security Design
/// Gate, item 30).
/// </summary>
public sealed class PasswordResetOptions
{
    public const string SectionName = "Identity:PasswordReset";

    public int TokenLifetimeMinutes { get; set; } = 30;
    public string FrontendResetUrlBase { get; set; } = "http://localhost:4200/reset-password";
}
