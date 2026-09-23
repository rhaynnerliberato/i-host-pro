namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Provider-neutral transactional email boundary (Self-Service Identity
/// &amp; Onboarding Foundation gate — Architecture + Security Design Gate,
/// approved). Application-layer code (e.g. Identity's password-reset
/// handler) depends only on this — never on a vendor SDK (SES/SendGrid/
/// Postmark/Resend/SMTP client) directly. Production Transactional Email
/// Provider gate: Resend is the selected/implemented production provider
/// (<c>ResendTransactionalEmailSender</c>), used for every non-Development
/// environment; Development keeps its own local Mailpit transport (see
/// <c>IHostPro.BuildingBlocks.Infrastructure.Email</c>). Password reset is
/// the only email currently sent through this boundary.
/// </summary>
public interface ITransactionalEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
