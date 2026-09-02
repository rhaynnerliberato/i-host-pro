using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting), Decision Gate §2/§27
/// — proves the "Authentication" HTTP category is genuinely wired through
/// the real ASP.NET Core pipeline (never just unit-tested against
/// <c>IDistributedRateLimiter</c> in isolation, which
/// <c>DistributedRateLimiterTests</c>, Configuration.Tests.Integration,
/// already covers for fairness/fail-open/fail-closed/thresholds). Deliberate
/// scope limit: the real per-IP partitioning ("IP A blocked, IP B
/// unaffected") is NOT re-proven here — <c>WebApplicationFactory</c>'s
/// in-memory <c>TestServer</c> transport reports the same synthetic loopback
/// <c>RemoteIpAddress</c> for every simulated request, so it cannot actually
/// simulate two distinct client IPs; partition correctness is proven once,
/// generically, at the <c>IDistributedRateLimiter</c> level instead. This
/// test proves the one thing that layer cannot: that the real
/// <c>[EnableRateLimiting("Authentication")]</c> attribute on
/// <c>AuthController.Login</c>, wired through <c>UseRateLimiter()</c> in
/// <c>IHostPro.Api</c>'s actual composition root, really does return 429
/// once the configured limit is reached — reusing the fixture's own
/// <c>ApiClient</c> (real <c>Program.cs</c>, real middleware pipeline).
/// </summary>
public sealed class AuthenticationRateLimitWorkflowRoundTripTests : IClassFixture<ConversationMessageReceivedWorkflowRoundTripTests.Fixture>
{
    private readonly ConversationMessageReceivedWorkflowRoundTripTests.Fixture _fixture;

    public AuthenticationRateLimitWorkflowRoundTripTests(ConversationMessageReceivedWorkflowRoundTripTests.Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Login_returns_429_once_the_Authentication_policys_configured_limit_is_exceeded()
    {
        // appsettings.json default: RateLimiting:Policies:Authentication:PermitLimit = 30.
        // Credentials are deliberately invalid — the rate limiter runs before
        // LoginCommandHandler, so every one of these returns 401 until the
        // limit trips, regardless of whether the account is real.
        var request = new { tenantSlug = "does-not-exist", email = "nobody@example.com", password = "wrong" };

        HttpResponseMessage? lastResponse = null;
        for (var i = 0; i < 31; i++)
            lastResponse = await _fixture.ApiClient.PostAsJsonAsync("/api/v1/auth/login", request);

        lastResponse!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the 31st call in this window must exceed the configured limit of 30");
        lastResponse.Headers.RetryAfter.Should().NotBeNull("mandate §25 — Retry-After must be set when the implementation can compute it");
    }
}
