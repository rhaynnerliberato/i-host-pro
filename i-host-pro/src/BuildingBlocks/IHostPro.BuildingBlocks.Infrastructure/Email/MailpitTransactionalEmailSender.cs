using IHostPro.BuildingBlocks.Application;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace IHostPro.BuildingBlocks.Infrastructure.Email;

/// <summary>
/// Development-only <see cref="ITransactionalEmailSender"/> — sends via
/// plain, unauthenticated SMTP to a local Mailpit instance (Self-Service
/// Identity &amp; Onboarding Foundation gate, approved Development
/// transport). Registered only when <c>isDevelopmentEnvironment</c> is true
/// (see <see cref="TransactionalEmailServiceCollectionExtensions"/>) —
/// never reachable in a real environment. Logs only safe delivery metadata
/// (recipient, subject) — the message body (which may carry a password-
/// reset link) is never logged, since Mailpit's own web UI is the intended
/// way to inspect it locally.
/// </summary>
public sealed class MailpitTransactionalEmailSender : ITransactionalEmailSender
{
    private readonly TransactionalEmailOptions _options;
    private readonly ILogger<MailpitTransactionalEmailSender> _logger;

    public MailpitTransactionalEmailSender(IOptions<TransactionalEmailOptions> options, ILogger<MailpitTransactionalEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        mimeMessage.To.Add(new MailboxAddress(message.ToName, message.ToAddress));
        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = new TextPart("plain") { Text = message.PlainTextBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, MailKit.Security.SecureSocketOptions.None, cancellationToken);
        await client.SendAsync(mimeMessage, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        _logger.LogInformation("Development email sent via Mailpit to {ToAddress} with subject {Subject}.", message.ToAddress, message.Subject);
    }
}
