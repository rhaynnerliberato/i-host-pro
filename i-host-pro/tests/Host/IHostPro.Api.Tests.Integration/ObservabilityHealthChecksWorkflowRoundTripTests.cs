using System.Net;
using FluentAssertions;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 12, Checkpoint 2 (Observability Finalization, Documento 21 §18).
/// Reuses <see cref="ConversationMessageReceivedWorkflowRoundTripTests.Fixture"/>
/// verbatim (own container instance, same shape as every other CP2-CP7 E2E
/// class in this directory) — proves the real <c>IHostPro.Api</c> process,
/// with real Postgres/RabbitMQ Testcontainers already up, actually reports
/// them as healthy dependencies, and that the response never leaks a
/// connection string, password, or raw exception detail.
/// </summary>
public sealed class ObservabilityHealthChecksWorkflowRoundTripTests : IClassFixture<ConversationMessageReceivedWorkflowRoundTripTests.Fixture>
{
    private readonly ConversationMessageReceivedWorkflowRoundTripTests.Fixture _fixture;

    public ObservabilityHealthChecksWorkflowRoundTripTests(ConversationMessageReceivedWorkflowRoundTripTests.Fixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Liveness_always_returns_200_and_never_touches_any_dependency()
    {
        var response = await _fixture.ApiClient.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // Predicate = _ => false means zero registered checks run — the
        // default ASP.NET Core writer for an empty report is the literal
        // word "Healthy", never JSON with component entries.
        body.Should().Be("Healthy");
    }

    [Fact]
    public async Task Readiness_reports_the_real_Postgres_and_RabbitMQ_Testcontainers_as_components()
    {
        var response = await _fixture.ApiClient.GetAsync("/health/ready");

        // Both Postgres and RabbitMQ are real, reachable Testcontainers in
        // this fixture — overall status must be either Healthy (both
        // "ready"-tagged checks pass) or, at worst, Degraded if Redis alone
        // (a non-critical, Degraded-only dependency) is unreachable in this
        // environment — never Unhealthy/503, which only a genuinely broken
        // Postgres/RabbitMQ would cause.
        response.StatusCode.Should().Be(HttpStatusCode.OK, "Postgres/RabbitMQ are real and reachable in this fixture, and a Redis-only failure is Degraded, never Unhealthy");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"name\":\"postgres\"");
        body.Should().Contain("\"name\":\"rabbitmq\"");
        body.Should().Contain("\"name\":\"redis\"");
        body.Should().Contain("\"status\":\"Healthy\"", "at minimum Postgres and RabbitMQ, both real and reachable here, must report Healthy");
    }

    [Fact]
    public async Task Readiness_response_never_leaks_a_connection_string_password_or_raw_exception_detail()
    {
        var response = await _fixture.ApiClient.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        // The response writer emits only name/status/durationMs per
        // component (ObservabilityHealthCheckResponseWriter) — never
        // HealthReportEntry.Description/Exception, which is exactly where a
        // raw Npgsql/StackExchange.Redis/RabbitMQ.Client exception message
        // (potentially embedding a connection string) would otherwise
        // appear.
        body.Should().NotContainEquivalentOf("password", "the safe response writer never serializes Description/Exception");
        body.Should().NotContainEquivalentOf("Host=", "never a raw Npgsql connection string fragment");
        body.Should().NotContain("amqp://", "never a raw RabbitMQ connection string/URI");
        body.Should().NotContain("Exception", "never a raw driver exception type/message");
    }

    [Fact]
    public async Task Preserved_health_endpoint_behaves_identically_to_ready_for_backward_compatibility()
    {
        var legacy = await _fixture.ApiClient.GetAsync("/health");
        var ready = await _fixture.ApiClient.GetAsync("/health/ready");

        legacy.StatusCode.Should().Be(ready.StatusCode);

        // Same predicate (every "ready"-tagged check) and same response
        // writer as /health/ready — compared structurally, never as an exact
        // string: each call re-runs the checks independently, so durationMs
        // legitimately differs by fractions of a millisecond between the two
        // real, separate requests.
        var legacyBody = await legacy.Content.ReadAsStringAsync();
        var readyBody = await ready.Content.ReadAsStringAsync();
        using var legacyJson = System.Text.Json.JsonDocument.Parse(legacyBody);
        using var readyJson = System.Text.Json.JsonDocument.Parse(readyBody);

        legacyJson.RootElement.GetProperty("status").GetString().Should().Be(readyJson.RootElement.GetProperty("status").GetString());
        var legacyNames = legacyJson.RootElement.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Order();
        var readyNames = readyJson.RootElement.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Order();
        legacyNames.Should().Equal(readyNames);
    }
}
