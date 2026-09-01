namespace IHostPro.Contexts.AIAgent.Tests.Unit.ModelProviders.Anthropic;

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("https://api.anthropic.com/"),
    };
}
