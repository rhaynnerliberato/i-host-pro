using Xunit;

// Multiple test classes in this assembly each bind a real Testcontainers
// RabbitMQ to the SAME fixed host port (5672) — required because the tests
// exercise the real IHostPro.Api/IHostPro.Worker subprocesses, which read
// RabbitMq:Host from configuration and cannot be pointed at a random
// container-assigned port without duplicating that wiring per test. xUnit
// parallelizes different test classes by default; without this, two classes'
// fixtures racing to bind port 5672 fail one of them with "port is already
// allocated" — a real, observed failure when running the whole assembly in
// one `dotnet test` invocation (never noticed before because these classes
// had always been run individually via --filter). Mirrors WebE2EFixture's
// own reason for forcing its test classes into one shared, sequential
// collection.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
