namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.AirbnbEmailBridge;

/// <summary>Mirrors <c>Meta.RecordingHttpMessageHandler</c> exactly, scoped to this test folder.</summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

    private RecordingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;

    public static RecordingHttpMessageHandler Returning(HttpResponseMessage response) =>
        new(_ => Task.FromResult(response));

    public static RecordingHttpMessageHandler With(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        new(responder);

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        return await _responder(request);
    }

    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? AuthorizationHeader, string? Body);
}
