namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.Meta;

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("https://graph.facebook.com/"),
    };
}
