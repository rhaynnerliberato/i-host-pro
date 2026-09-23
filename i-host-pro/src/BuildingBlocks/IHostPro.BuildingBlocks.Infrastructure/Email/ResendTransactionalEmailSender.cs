using System.Net.Http.Headers;
using System.Net.Http.Json;
using IHostPro.BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IHostPro.BuildingBlocks.Infrastructure.Email;

/// <summary>
/// Production <see cref="ITransactionalEmailSender"/> — sends via the Resend
/// HTTP API (Production Transactional Email Provider gate). A plain
/// <see cref="IHttpClientFactory"/> call, not the Resend SDK: this codebase's
/// established convention for a third-party API this small (mirrors
/// <c>MicrosoftGraphEmailMessageSource</c>) rather than a new package
/// dependency. Registered outside Development only (see
/// <see cref="TransactionalEmailServiceCollectionExtensions"/>) — fails
/// loudly on first use if <see cref="ResendOptions.ApiKey"/> is missing,
/// never a silent no-op or a silent fallback to Mailpit.
/// </summary>
public sealed class ResendTransactionalEmailSender : ITransactionalEmailSender
{
    public const string HttpClientName = "TransactionalEmail.Resend";
    private const string SendEndpoint = "https://api.resend.com/emails";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResendOptions _options;
    private readonly ILogger<ResendTransactionalEmailSender> _logger;

    public ResendTransactionalEmailSender(
        IHttpClientFactory httpClientFactory, IOptions<ResendOptions> options, ILogger<ResendTransactionalEmailSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "Resend is not configured (missing Resend:ApiKey) - this is a required production dependency, never a silent no-op.");
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            from = $"{_options.FromName} <{_options.FromAddress}>",
            to = new[] { message.ToAddress },
            subject = message.Subject,
            text = message.PlainTextBody,
        });

        var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Never log the response body - Resend echoes request fields
            // (including "to") back in error payloads.
            throw new InvalidOperationException($"Resend API returned HTTP {(int)response.StatusCode}.");
        }

        _logger.LogInformation("Production email sent via Resend to {ToAddress} with subject {Subject}.", message.ToAddress, message.Subject);
    }
}
