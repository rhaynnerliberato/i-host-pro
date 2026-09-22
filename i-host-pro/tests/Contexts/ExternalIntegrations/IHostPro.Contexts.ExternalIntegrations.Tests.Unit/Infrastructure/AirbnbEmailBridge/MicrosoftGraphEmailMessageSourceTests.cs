using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.AirbnbEmailBridge;

public class MicrosoftGraphEmailMessageSourceTests
{
    private static MicrosoftGraphEmailMessageSource BuildSource(RecordingHttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<MicrosoftGraphEmailMessageSource>.Instance);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body) };

    [Fact]
    public async Task Initial_sync_with_no_cursor_requests_the_well_known_folder_with_select()
    {
        var handler = RecordingHttpMessageHandler.Returning(JsonResponse(HttpStatusCode.OK, new { value = Array.Empty<object>() }));
        var source = BuildSource(handler);

        await source.GetDeltaPageAsync("token-1", "inbox", null, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Uri.ToString().Should().StartWith("https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?");
        request.Uri.ToString().Should().Contain("$select=id,internetMessageId,receivedDateTime,subject,from,sender,bodyPreview");
        request.AuthorizationHeader.Should().Be("Bearer token-1");
    }

    [Fact]
    public async Task Subsequent_sync_uses_the_provided_cursor_verbatim_with_no_extra_parameters()
    {
        const string cursor = "https://graph.microsoft.com/v1.0/me/mailFolders('inbox')/messages/delta?$skiptoken=OPAQUE123";
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, new { value = Array.Empty<object>() }));
        var source = BuildSource(handler);

        await source.GetDeltaPageAsync("token-1", "inbox", cursor, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.ToString().Should().Be(cursor, "a caller-supplied deltaLink/nextLink is opaque and must never be rewritten");
    }

    [Fact]
    public async Task A_non_final_page_returns_the_nextLink_and_no_deltaLink()
    {
        const string body = """{"@odata.nextLink":"https://graph.microsoft.com/v1.0/next-page","value":[{"id":"msg-1","internetMessageId":"<msg-1@mail>","receivedDateTime":"2026-09-16T10:00:00Z","subject":"Reservation confirmed","from":{"emailAddress":{"address":"automated@airbnb.com"}},"sender":{"emailAddress":{"address":"automated@airbnb.com"}},"bodyPreview":"Your reservation is confirmed"}]}""";
        var handler = RecordingHttpMessageHandler.Returning(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        var source = BuildSource(handler);

        var outcome = await source.GetDeltaPageAsync("token-1", "inbox", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Page!.NextLink.Should().Be("https://graph.microsoft.com/v1.0/next-page");
        outcome.Page.DeltaLink.Should().BeNull();
        outcome.Page.Messages.Should().ContainSingle();
        outcome.Page.Messages[0].MessageId.Should().Be("msg-1");
        outcome.Page.Messages[0].InternetMessageId.Should().Be("<msg-1@mail>");
        outcome.Page.Messages[0].Subject.Should().Be("Reservation confirmed");
        outcome.Page.Messages[0].FromAddress.Should().Be("automated@airbnb.com");
    }

    [Fact]
    public async Task The_final_page_returns_a_deltaLink_and_no_nextLink()
    {
        const string body = """{"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta-cursor","value":[]}""";
        var handler = RecordingHttpMessageHandler.Returning(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        var source = BuildSource(handler);

        var outcome = await source.GetDeltaPageAsync("token-1", "inbox", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Page!.DeltaLink.Should().Be("https://graph.microsoft.com/v1.0/delta-cursor");
        outcome.Page.NextLink.Should().BeNull();
    }

    [Fact]
    public async Task A_429_response_is_retried_after_the_Retry_After_delay_and_then_succeeds()
    {
        var attempt = 0;
        var handler = RecordingHttpMessageHandler.With(_ =>
        {
            attempt++;
            if (attempt == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return Task.FromResult(throttled);
            }

            const string body = """{"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta-cursor","value":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var source = BuildSource(handler);

        var outcome = await source.GetDeltaPageAsync("token-1", "inbox", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        attempt.Should().Be(2, "the first 429 must be retried exactly once before succeeding");
    }

    [Fact]
    public async Task Http_401_maps_to_AuthenticationFailed()
    {
        var handler = RecordingHttpMessageHandler.Returning(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var source = BuildSource(handler);

        var outcome = await source.GetDeltaPageAsync("token-1", "inbox", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.FailureReason.Should().Be(AirbnbEmailDeltaFetchFailureReason.AuthenticationFailed);
    }

    [Fact]
    public async Task Http_410_with_a_resync_required_body_maps_to_InvalidDeltaLink()
    {
        var body = """{"error":{"code":"ResyncRequired","message":"..."}}""";
        var handler = RecordingHttpMessageHandler.Returning(new HttpResponseMessage(HttpStatusCode.Gone)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var source = BuildSource(handler);

        var outcome = await source.GetDeltaPageAsync("token-1", "inbox", "https://graph.microsoft.com/v1.0/stale-delta", CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.FailureReason.Should().Be(AirbnbEmailDeltaFetchFailureReason.InvalidDeltaLink);
    }

    [Fact]
    public async Task Malformed_json_maps_to_a_transient_failure_never_throws()
    {
        var handler = RecordingHttpMessageHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json"),
        });
        var source = BuildSource(handler);

        var outcome = await source.GetDeltaPageAsync("token-1", "inbox", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.FailureReason.Should().Be(AirbnbEmailDeltaFetchFailureReason.TransientFailure);
    }

    [Fact]
    public async Task A_network_error_maps_to_a_transient_failure_never_throws()
    {
        var handler = RecordingHttpMessageHandler.With(_ => throw new HttpRequestException("simulated network failure"));
        var source = BuildSource(handler);

        var outcome = await source.GetDeltaPageAsync("token-1", "inbox", null, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.FailureReason.Should().Be(AirbnbEmailDeltaFetchFailureReason.TransientFailure);
    }

    [Fact]
    public async Task GetMessageContentAsync_fetches_subject_and_body_in_one_request()
    {
        const string body = """{"subject":"Reservation confirmed","body":{"content":"<html>full body</html>"}}""";
        var handler = RecordingHttpMessageHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var source = BuildSource(handler);

        var content = await source.GetMessageContentAsync("token-1", "msg-1", CancellationToken.None);

        handler.Requests.Should().ContainSingle("subject and body must be fetched in the same Graph request, never two separate calls");
        var request = handler.Requests[0];
        request.Uri.ToString().Should().Be("https://graph.microsoft.com/v1.0/me/messages/msg-1?$select=subject,body");
        request.AuthorizationHeader.Should().Be("Bearer token-1");
        content.Should().NotBeNull();
        content!.Subject.Should().Be("Reservation confirmed");
        content.Body.Should().Be("<html>full body</html>");
    }

    [Fact]
    public async Task GetMessageContentAsync_returns_null_when_the_message_no_longer_exists()
    {
        var handler = RecordingHttpMessageHandler.Returning(new HttpResponseMessage(HttpStatusCode.NotFound));
        var source = BuildSource(handler);

        var content = await source.GetMessageContentAsync("token-1", "msg-1", CancellationToken.None);

        content.Should().BeNull();
    }
}
