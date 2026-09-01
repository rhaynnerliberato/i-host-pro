namespace IHostPro.Contexts.AIAgent.Tests.Unit.ModelProviders.Anthropic;

/// <summary>
/// Deterministic, no-live-internet double for the one HTTP call
/// <c>AnthropicModelProvider</c> makes (Fase 11, Checkpoint 7, mandate item
/// 62 — "fake/local HTTP server", mirrors <c>RecordingHttpMessageHandler</c>
/// from ExternalIntegrations.Tests.Unit exactly, adapted to capture every
/// request header — not just <c>Authorization</c> — since Anthropic uses
/// <c>x-api-key</c>/<c>anthropic-version</c> instead of a Bearer token).
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

    private RecordingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;

    public static RecordingHttpMessageHandler Returning(HttpResponseMessage response) =>
        new(_ => Task.FromResult(response));

    public static RecordingHttpMessageHandler Throwing(Exception exception) =>
        new(_ => throw exception);

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

        return await _responder(request);
    }

    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);
}
