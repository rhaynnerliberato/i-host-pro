using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.ModelProviders.Anthropic;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting), Decision Gate
/// amendment (ADR-031) — deterministic proof of the official
/// (<c>Microsoft.Extensions.Http.Resilience</c>) circuit breaker states and
/// boundaries, mirroring exactly the pipeline shape
/// <c>AIAgentModuleExtensions</c> wires for the real Anthropic
/// <see cref="System.Net.Http.HttpClient"/> (<c>AddCircuitBreaker</c> only —
/// never <c>AddRetry</c>/<c>AddHedging</c>/<c>AddTimeout</c>). Exercises the
/// pipeline directly against a real <see cref="IHttpClientFactory"/> built
/// from a real <see cref="ServiceCollection"/> — never
/// <c>AnthropicModelProvider</c>'s own <see cref="FakeHttpClientFactory"/>,
/// which bypasses the resilience pipeline entirely and so cannot prove
/// anything about it. Zero live network — <see cref="RecordingHttpMessageHandler"/>
/// only.
/// </summary>
public class AnthropicCircuitBreakerTests
{
    // Polly's own CircuitBreakerStrategyOptions validation requires
    // BreakDuration >= 500ms — the shortest value that keeps this test fast
    // while staying valid.
    private static readonly TimeSpan BreakDuration = TimeSpan.FromMilliseconds(500);

    private static (IHttpClientFactory Factory, RecordingHttpMessageHandler Handler) BuildFactory(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var handler = RecordingHttpMessageHandler.With(responder);
        var services = new ServiceCollection();

        services.AddHttpClient("test-client")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddResilienceHandler("test-circuit-breaker", builder =>
            {
                builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 2,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    BreakDuration = BreakDuration,
                    // Mirrors AnthropicModelProvider.IsPermanentFailure exactly:
                    // network errors/timeouts/429/5xx count; 4xx never does.
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Exception is HttpRequestException or TaskCanceledException ||
                        (args.Outcome.Result is { } response &&
                            (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))),
                });
            });

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>(), handler);
    }

    [Fact]
    public async Task Closed_circuit_allows_calls_through()
    {
        var (factory, handler) = BuildFactory(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = factory.CreateClient("test-client");

        var response = await client.GetAsync("https://example.test/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Consecutive_transient_failures_open_the_circuit_and_reject_without_a_new_HTTP_request()
    {
        var (factory, handler) = BuildFactory(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = factory.CreateClient("test-client");

        await client.GetAsync("https://example.test/");
        await client.GetAsync("https://example.test/");
        handler.Requests.Should().HaveCount(2, "MinimumThroughput=2/FailureRatio=0.5 — 2 real 500s must open the circuit");

        Func<Task> act = () => client.GetAsync("https://example.test/");
        await act.Should().ThrowAsync<BrokenCircuitException>();
        handler.Requests.Should().HaveCount(2, "an OPEN circuit must reject locally — never a 3rd real HTTP attempt");
    }

    [Fact]
    public async Task Circuit_closes_again_after_a_successful_probe_once_the_break_duration_elapses()
    {
        var succeedFromNowOn = false;
        var (factory, handler) = BuildFactory(_ => Task.FromResult(
            succeedFromNowOn ? new HttpResponseMessage(HttpStatusCode.OK) : new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = factory.CreateClient("test-client");

        await client.GetAsync("https://example.test/");
        await client.GetAsync("https://example.test/");
        // Circuit is OPEN.

        succeedFromNowOn = true;
        await Task.Delay(BreakDuration + TimeSpan.FromMilliseconds(300));

        var probeResponse = await client.GetAsync("https://example.test/");
        probeResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the HALF-OPEN probe call is a real HTTP request that now succeeds");
        handler.Requests.Should().HaveCount(3);

        var nextResponse = await client.GetAsync("https://example.test/");
        nextResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the circuit is CLOSED again — normal calls flow");
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task A_failed_probe_sends_the_circuit_back_to_open()
    {
        var (factory, handler) = BuildFactory(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = factory.CreateClient("test-client");

        await client.GetAsync("https://example.test/");
        await client.GetAsync("https://example.test/");
        await Task.Delay(BreakDuration + TimeSpan.FromMilliseconds(300));

        var probeResponse = await client.GetAsync("https://example.test/");
        probeResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        handler.Requests.Should().HaveCount(3, "the probe itself is a real HTTP attempt, even though it fails");

        Func<Task> act = () => client.GetAsync("https://example.test/");
        await act.Should().ThrowAsync<BrokenCircuitException>();
        handler.Requests.Should().HaveCount(3, "a failed probe re-opens the circuit — the next call is rejected locally again");
    }

    [Fact]
    public async Task A_permanent_4xx_failure_never_opens_the_circuit()
    {
        var (factory, handler) = BuildFactory(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = factory.CreateClient("test-client");

        for (var i = 0; i < 5; i++)
            await client.GetAsync("https://example.test/");

        handler.Requests.Should().HaveCount(5, "a permanent 401 (mirrors IsPermanentFailure) must never count toward opening the circuit");
    }

    [Fact]
    public async Task One_transient_failure_produces_exactly_one_HTTP_request_never_a_second()
    {
        var (factory, handler) = BuildFactory(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = factory.CreateClient("test-client");

        await client.GetAsync("https://example.test/");

        handler.Requests.Should().ContainSingle(
            "AutomaticHttpResilienceRetry=false — only AddCircuitBreaker is configured, never AddRetry/AddHedging");
    }
}
