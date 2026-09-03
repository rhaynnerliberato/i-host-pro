using System.Net;

namespace IHostPro.RabbitMqCredentialRotation.Tests.Unit;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public static FakeHttpMessageHandler AlwaysReturns(HttpStatusCode statusCode, string body = "{}") =>
        new(_ => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) }));

    public static FakeHttpMessageHandler Throws(Exception exception) =>
        new(_ => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        return await _respond(request);
    }
}
