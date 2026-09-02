namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.Meta;

/// <summary>
/// Deterministic, no-live-internet double for the one HTTP call
/// <c>MetaWhatsAppMessagingProvider</c> makes (Fase 9, Checkpoint 2.2 mandate
/// §39: "custom HttpMessageHandler/TestServer/existing fixture, no new
/// WireMock package"). Records every request it sees (method, URL, headers,
/// body) so tests can assert the exact wire shape, and returns whatever
/// response/exception the test programs.
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

    private RecordingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;

    public static RecordingHttpMessageHandler Returning(HttpResponseMessage response) =>
        new(_ => Task.FromResult(response));

    public static RecordingHttpMessageHandler Throwing(Exception exception) =>
        new(_ => throw exception);

    /// <summary>Fase 12, Checkpoint 3 — for circuit breaker tests that need a different response per call.</summary>
    public static RecordingHttpMessageHandler With(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        new(responder);

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        return await _responder(request);
    }

    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? AuthorizationHeader, string? Body);
}
