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

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 2.3.3 mandate §31: a valid, normalized status callback
/// whose durable-publish step fails transiently must never make the
/// endpoint return a false success — the controller lets the exception
/// propagate uncaught, and ASP.NET Core's own default unhandled-exception
/// behavior turns that into a 5xx, which is exactly what makes Meta retry
/// the delivery. Never simulates Meta itself — only the HTTP request/
/// signature Meta would send, and a fake publisher standing in for a real
/// transient outbox failure (already proven for real by
/// <c>WhatsAppMessageStatusChangedWorkerRoundTripTests</c>).
/// </summary>
public sealed class WhatsAppWebhookStatusDurabilityHttpTests : IAsyncDisposable
{
    private const string AppSecret = "test-app-secret";
    private const string VerifyToken = "test-verify-token";
    private const string KnownPhoneNumberId = "known-phone-id";
    private static readonly Guid KnownTenantId = Guid.NewGuid();

    private readonly IHost _host;
    private readonly HttpClient _client;

    public WhatsAppWebhookStatusDurabilityHttpTests()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddControllers().AddApplicationPart(typeof(WhatsAppWebhookController).Assembly);
                    services.AddAuthorization();
                    services.AddSingleton<IWhatsAppWebhookCredentialProvider>(
                        new FakeWebhookCredentialProvider(AppSecret, VerifyToken));
                    services.AddSingleton<IWebhookSignatureVerifier, MetaWebhookSignatureVerifier>();
                    services.AddSingleton<IWhatsAppTenantRouteResolver>(new FixedTenantRouteResolver(KnownPhoneNumberId, KnownTenantId));
                    services.AddScoped<IWhatsAppWebhookStatusProcessor, MetaWebhookStatusProcessor>();
                    services.AddSingleton<IWhatsAppWebhookStatusEventPublisher>(
                        new ThrowingStatusEventPublisher(new InvalidOperationException("simulated transient outbox failure")));
                    services.AddLogging();
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

    [Fact]
    public async Task A_transient_publish_failure_never_returns_a_false_2xx()
    {
        // TestServer's own HttpClient rethrows an unhandled server-side
        // exception directly to the caller instead of translating it into an
        // HTTP response (a documented TestServer-specific debugging
        // convenience — real Kestrel-hosted IHostPro.Api has no exception-
        // handling middleware of its own either, confirmed by inspecting
        // Program.cs, so a real deployment falls back to ASP.NET Core
        // hosting's own default: log and return 500). What matters here,
        // and what IS provable through TestServer, is the controller's own
        // contract: the failure must propagate untouched, never be caught
        // and converted into Ok() — a silent false success.
        var body = BuildStatusPayload(KnownPhoneNumberId, "wamid.ABC", "delivered", "1750030073");
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var act = () => _client.SendAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated transient outbox failure");
    }

    private static string BuildStatusPayload(string phoneNumberId, string id, string status, string timestamp) =>
        "{\"entry\":[{\"changes\":[{\"value\":{" +
        "\"metadata\":{\"phone_number_id\":\"" + phoneNumberId + "\"}," +
        "\"statuses\":[{\"id\":\"" + id + "\",\"status\":\"" + status + "\",\"timestamp\":\"" + timestamp + "\"}]" +
        "}}]}]}";

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

    private sealed class FixedTenantRouteResolver(string knownPhoneNumberId, Guid knownTenantId) : IWhatsAppTenantRouteResolver
    {
        public Task<Guid?> ResolveTenantIdAsync(string phoneNumberId, CancellationToken cancellationToken) =>
            Task.FromResult(phoneNumberId == knownPhoneNumberId ? (Guid?)knownTenantId : null);
    }

    private sealed class ThrowingStatusEventPublisher(Exception exception) : IWhatsAppWebhookStatusEventPublisher
    {
        public Task PublishAsync(WebhookStatusProcessingOutcome outcome, CancellationToken cancellationToken) =>
            throw exception;
    }
}
