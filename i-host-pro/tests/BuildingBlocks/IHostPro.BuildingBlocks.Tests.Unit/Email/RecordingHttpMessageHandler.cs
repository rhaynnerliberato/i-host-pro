namespace IHostPro.BuildingBlocks.Tests.Unit.Email;

/// <summary>Deterministic, no-live-internet double for the one HTTP call <c>ResendTransactionalEmailSender</c> makes — mirrors the ExternalIntegrations test project's own equivalent helper.</summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

    private RecordingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;

    public static RecordingHttpMessageHandler Returning(HttpResponseMessage response) => new(_ => Task.FromResult(response));

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        return await _responder(request);
    }

    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? AuthorizationHeader, string? Body);
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
