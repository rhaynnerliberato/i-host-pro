using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IHostPro.BuildingBlocks.Tests.Unit.Email;

public class ResendTransactionalEmailSenderTests
{
    private static ResendTransactionalEmailSender BuildSender(RecordingHttpMessageHandler handler, ResendOptions? options = null) =>
        new(new FakeHttpClientFactory(handler),
            Options.Create(options ?? new ResendOptions { ApiKey = "re_test_key", FromAddress = "no-reply@ihostpro.com", FromName = "iHostPro" }),
            NullLogger<ResendTransactionalEmailSender>.Instance);

    [Fact]
    public async Task SendAsync_posts_the_correct_recipient_subject_sender_and_bearer_token()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "abc-123" }) });
        var sender = BuildSender(handler);
        var message = new EmailMessage("guest@example.com", "Guest", "Redefinição de senha — iHostPro", "corpo com link de reset");

        await sender.SendAsync(message, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://api.resend.com/emails");
        request.AuthorizationHeader.Should().Be("Bearer re_test_key");

        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("to")[0].GetString().Should().Be("guest@example.com");
        body.RootElement.GetProperty("subject").GetString().Should().Be("Redefinição de senha — iHostPro");
        body.RootElement.GetProperty("from").GetString().Should().Be("iHostPro <no-reply@ihostpro.com>");
        body.RootElement.GetProperty("text").GetString().Should().Be("corpo com link de reset");
    }

    [Fact]
    public async Task SendAsync_never_includes_the_api_key_in_the_request_body()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "abc-123" }) });
        var sender = BuildSender(handler, new ResendOptions { ApiKey = "re_super_secret", FromAddress = "no-reply@ihostpro.com", FromName = "iHostPro" });

        await sender.SendAsync(new EmailMessage("guest@example.com", "Guest", "Subject", "Body"), CancellationToken.None);

        handler.Requests[0].Body.Should().NotContain("re_super_secret", "the API key must only ever appear in the Authorization header, never in the request body");
    }

    [Fact]
    public async Task SendAsync_throws_when_Resend_returns_a_non_success_status()
    {
        var handler = RecordingHttpMessageHandler.Returning(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var sender = BuildSender(handler);

        var act = () => sender.SendAsync(new EmailMessage("guest@example.com", "Guest", "Subject", "Body"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SendAsync_throws_and_never_calls_Resend_when_the_api_key_is_not_configured()
    {
        var handler = RecordingHttpMessageHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK));
        var sender = BuildSender(handler, new ResendOptions { ApiKey = "", FromAddress = "no-reply@ihostpro.com", FromName = "iHostPro" });

        var act = () => sender.SendAsync(new EmailMessage("guest@example.com", "Guest", "Subject", "Body"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Resend:ApiKey*");
        handler.Requests.Should().BeEmpty();
    }
}
