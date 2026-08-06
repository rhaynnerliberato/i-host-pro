namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Forces every test class that depends on <see cref="WebE2EFixture"/> to
/// share a single instance and run sequentially, never in parallel with each
/// other — xUnit parallelizes across collections by default, and a second,
/// concurrently-booting <see cref="WebE2EFixture"/> would fail outright
/// (its RabbitMQ container binds the fixed host port 5672, which only one
/// instance can hold at a time).
/// </summary>
[CollectionDefinition(Name)]
public sealed class WebE2EFixtureCollection : ICollectionFixture<WebE2EFixture>
{
    public const string Name = "WebE2E";
}
