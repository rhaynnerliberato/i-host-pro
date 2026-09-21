namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public request body for <c>POST /api/v1/auth/forgot-password/complete</c>
/// (Self-Service Identity &amp; Onboarding Foundation gate). Never carries a
/// tenant id — it is recovered exclusively from the token itself once
/// atomically consumed.
/// </summary>
public sealed record ForgotPasswordCompleteRequest(string? Token, string? NewPassword)
{
    public override string ToString() =>
        $"{nameof(ForgotPasswordCompleteRequest)} {{ {nameof(Token)} = [REDACTED], {nameof(NewPassword)} = [REDACTED] }}";
}
