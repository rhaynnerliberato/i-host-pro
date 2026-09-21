namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Provider-neutral transactional email boundary (Self-Service Identity
/// &amp; Onboarding Foundation gate — Architecture + Security Design Gate,
/// approved). Application-layer code (e.g. Identity's password-reset
/// handler) depends only on this — never on a vendor SDK (SES/SendGrid/
/// Postmark/Resend/SMTP client) directly. The real production
/// implementation is deliberately deferred (no live production environment
/// exists yet, AWS remains paused) — only a Development transport exists
/// today (see <c>IHostPro.BuildingBlocks.Infrastructure.Email</c>).
/// </summary>
public interface ITransactionalEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
