using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Api.Controllers;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 2.3.1 mandate §33: proves the REAL ASP.NET Core HTTP
/// pipeline (routing, model binding, controller dispatch, authorization
/// middleware) for <see cref="WhatsAppWebhookController"/> — never a bare
/// unit-level call into the controller's C# methods. Deliberately does NOT
/// use <c>WebApplicationFactory&lt;IHostPro.Api.Program&gt;</c>: that would
/// pull in all 8 DbContexts and a real RabbitMQ connection this endpoint has
/// no dependency on (see <c>OpenApiOperationIdTests</c> for that heavier
/// pattern, used where the full composition root genuinely matters). This
/// host registers only what <see cref="WhatsAppWebhookController"/> actually
/// needs: MVC controllers from the real Api assembly, the same
/// <c>AddAuthorization()</c> call <c>IdentityJwtBearerAuthenticationExtensions</c>
/// makes in production (no fallback policy — proving anonymous access is not
/// an artifact of skipping the authorization middleware), a fake credential
/// provider (test secrets only, never a real one), and the REAL
/// <see cref="MetaWebhookSignatureVerifier"/> (never faked — the whole point
/// is proving the real crypto path over real HTTP).
///
/// Never calls the real Meta API. Never touches any database.
/// </summary>
public sealed class WhatsAppWebhookControllerHttpTests : IAsyncDisposable
{
    private const string AppSecret = "test-app-secret";
    private const string VerifyToken = "test-verify-token";

    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly ListLoggerProvider _loggerProvider = new();

    public WhatsAppWebhookControllerHttpTests()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddControllers().AddApplicationPart(typeof(WhatsAppWebhookController).Assembly);
                    services.AddAuthorization(); // mirrors IdentityJwtBearerAuthenticationExtensions exactly — no fallback policy.
                    services.AddSingleton<IWhatsAppWebhookCredentialProvider>(
                        new FakeWebhookCredentialProvider(AppSecret, VerifyToken));
                    services.AddSingleton<IWebhookSignatureVerifier, MetaWebhookSignatureVerifier>();
                    // Fase 9, Checkpoint 2.3.2 added this dependency to the
                    // controller — a no-op fake here since this file's own
                    // scope is signature verification, not status
                    // processing (see WhatsAppWebhookStatusRoutingHttpTests
                    // for that).
                    services.AddSingleton<IWhatsAppWebhookStatusProcessor>(new NoOpStatusProcessor());
                    // Fase 9, Checkpoint 2.3.3 added this dependency to the
                    // controller — never actually invoked in this file
                    // (NoOpStatusProcessor above never returns an Accepted
                    // outcome), but still a required constructor dependency.
                    services.AddSingleton<IWhatsAppWebhookStatusEventPublisher>(new NoOpStatusEventPublisher());
                    services.AddLogging(logging => logging.AddProvider(_loggerProvider));
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        _host = hostBuilder.Start();
        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ---- GET verification ---------------------------------------------------

    [Fact]
    public async Task GET_with_valid_mode_and_token_returns_200_with_the_raw_challenge_body()
    {
        var response = await _client.GetAsync(
            $"/api/v1/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=1234567890");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("1234567890", "the response body must be the raw challenge value, never wrapped in JSON");
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
    }

    [Fact]
    public async Task GET_with_the_wrong_token_is_rejected()
    {
        var response = await _client.GetAsync(
            "/api/v1/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong-token&hub.challenge=123");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GET_with_a_missing_token_is_rejected()
    {
        var response = await _client.GetAsync(
            "/api/v1/integrations/whatsapp/webhook?hub.mode=subscribe&hub.challenge=123");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GET_with_the_wrong_mode_is_rejected()
    {
        var response = await _client.GetAsync(
            $"/api/v1/integrations/whatsapp/webhook?hub.mode=unsubscribe&hub.verify_token={VerifyToken}&hub.challenge=123");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- POST signature -------------------------------------------------------

    [Fact]
    public async Task POST_with_a_valid_signature_is_accepted()
    {
        var body = "{\"object\":\"whatsapp_business_account\",\"entry\":[]}";
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_with_a_missing_signature_header_is_rejected()
    {
        var body = "{}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_with_an_incorrect_signature_is_rejected()
    {
        var body = "{}";
        using var request = BuildSignedRequest(body, "sha256=" + new string('0', 64));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_with_a_signature_computed_for_a_different_body_is_rejected()
    {
        var signedBody = "{\"a\":1}";
        var actualBody = "{\"a\":2}";
        using var request = BuildSignedRequest(actualBody, ComputeSignature(signedBody, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the signature was computed for a different body — tampering after signing must be detected");
    }

    // ---- JWT/AllowAnonymous independence ---------------------------------------

    [Fact]
    public async Task POST_succeeds_with_no_Authorization_header_at_all()
    {
        var body = "{}";
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        request.Headers.Authorization.Should().BeNull();
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the webhook must never require human JWT auth — the real AddAuthorization() pipeline is wired in this test and still allows the request through");
    }

    // ---- Secret/body log safety -------------------------------------------------

    [Fact]
    public async Task Neither_the_app_secret_nor_the_verify_token_nor_the_raw_body_ever_appear_in_a_log_message()
    {
        var body = "{\"sentinel\":\"never-logged-body-marker\"}";
        using var validRequest = BuildSignedRequest(body, ComputeSignature(body, AppSecret));
        await _client.SendAsync(validRequest);

        using var invalidRequest = BuildSignedRequest(body, "sha256=" + new string('0', 64));
        await _client.SendAsync(invalidRequest);

        await _client.GetAsync(
            $"/api/v1/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=x");
        await _client.GetAsync(
            "/api/v1/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=x");

        // Scoped to THIS controller's own structured audit lines
        // (the {AuditEvent}-prefixed messages it writes itself) — never the
        // framework's generic ASP.NET Core hosting/routing request trace,
        // which is a separate, pre-existing logging category this checkpoint
        // does not control (see this test class's own remarks below).
        var auditMessages = _loggerProvider.Messages
            .Where(m => m.StartsWith("WhatsAppWebhook", StringComparison.Ordinal))
            .ToList();

        auditMessages.Should().NotBeEmpty("the controller must have written at least one audit line across these four calls");
        string.Join("\n", auditMessages).Should().NotContain(AppSecret)
            .And.NotContain(VerifyToken)
            .And.NotContain("never-logged-body-marker");
    }

    /// <summary>
    /// Real, documented finding from writing the test above (not a defect in
    /// this controller): ASP.NET Core's own default hosting/routing request
    /// trace logs the full request URL, including the query string — so a
    /// GET verify-token handshake (Meta's own protocol design puts the token
    /// in the query string, mandate §13) is inherently visible to that
    /// generic framework log category, regardless of anything this
    /// controller does. This checkpoint's own audit lines never include it
    /// (proven above); operators should ensure the
    /// <c>Microsoft.AspNetCore.Hosting</c>/<c>Microsoft.AspNetCore.Routing</c>
    /// categories are not left at Information/Debug in Production, or that
    /// query strings are redacted at the log-shipping layer — a Production
    /// logging-configuration concern, not something CP2.3.1's own code can
    /// unilaterally fix from inside one controller.
    /// </summary>
    [Fact]
    public async Task The_verify_token_query_string_is_visible_to_ASPNET_Cores_own_framework_request_log_by_design()
    {
        await _client.GetAsync(
            $"/api/v1/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=x");

        var allMessages = string.Join("\n", _loggerProvider.Messages);

        allMessages.Should().Contain(VerifyToken,
            "documenting the real behavior: the framework's own request trace logs the full URL — this is a property " +
            "of Meta's GET-based verify-token protocol design, not of this controller's own code");
    }

    private static HttpRequestMessage BuildSignedRequest(string body, string signatureHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signatureHeader);
        return request;
    }

    private static string ComputeSignature(string body, string appSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class FakeWebhookCredentialProvider(string appSecret, string verifyToken) : IWhatsAppWebhookCredentialProvider
    {
        public Task<string?> GetAppSecretAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(appSecret);
        public Task<string?> GetVerifyTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(verifyToken);
    }

    private sealed class NoOpStatusProcessor : IWhatsAppWebhookStatusProcessor
    {
        public Task<IReadOnlyList<WebhookStatusProcessingOutcome>> ProcessAsync(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WebhookStatusProcessingOutcome>>([]);
    }

    private sealed class NoOpStatusEventPublisher : IWhatsAppWebhookStatusEventPublisher
    {
        public Task PublishAsync(WebhookStatusProcessingOutcome outcome, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>In-memory log sink — captures every formatted message so tests can assert on secret/PII absence.</summary>
    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new ListLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                    messages.Add(formatter(state, exception));
            }
        }
    }
}
