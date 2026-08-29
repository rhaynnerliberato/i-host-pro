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
/// Fase 9, Checkpoint 2.3.2: proves the real HTTP pipeline for the
/// route-resolution/status-normalization slice added on top of Checkpoint
/// 2.3.1's security ingress — known route, unknown route, and malformed
/// status entries, all still returning 2xx (mandate §39-41). Also extends
/// the "zero tenant-owned access before signature" proof from CP2.3.1 to
/// cover route resolution: an invalid signature must never even call
/// <see cref="IWhatsAppTenantRouteResolver"/> (mandate §26/§33).
///
/// Same deliberately lightweight TestServer approach as
/// <c>WhatsAppWebhookControllerHttpTests</c> — no database, a fake route
/// resolver standing in for the real Postgres-backed one (already proven
/// separately in <c>ExternalIntegrationsFoundationTests</c>).
/// </summary>
public sealed class WhatsAppWebhookStatusRoutingHttpTests : IAsyncDisposable
{
    private const string AppSecret = "test-app-secret";
    private const string VerifyToken = "test-verify-token";
    private const string KnownPhoneNumberId = "known-phone-id";
    private static readonly Guid KnownTenantId = Guid.NewGuid();

    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly ListLoggerProvider _loggerProvider = new();
    private readonly SpyTenantRouteResolver _routeResolver = new();
    private readonly SpyStatusEventPublisher _eventPublisher = new();

    public WhatsAppWebhookStatusRoutingHttpTests()
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
                    services.AddSingleton<IWhatsAppTenantRouteResolver>(_routeResolver);
                    services.AddScoped<IWhatsAppWebhookStatusProcessor, MetaWebhookStatusProcessor>();
                    // Fase 9, Checkpoint 2.3.3 added this dependency — a spy
                    // here since this file's own scope is route resolution/
                    // status normalization, not durable event publishing
                    // (see WhatsAppWebhookStatusDurabilityHttpTests for that).
                    services.AddSingleton<IWhatsAppWebhookStatusEventPublisher>(_eventPublisher);
                    services.AddSingleton<IWhatsAppWebhookMessageProcessor>(new NoOpMessageProcessor());
                    services.AddSingleton<IWhatsAppWebhookMessageEventPublisher>(new NoOpMessageEventPublisher());
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

    [Fact]
    public async Task A_known_route_status_payload_is_accepted_and_audited()
    {
        var body = BuildStatusPayload(KnownPhoneNumberId, "wamid.ABC", "delivered", "1750030073");
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _loggerProvider.Messages.Should().Contain(m => m.Contains("WhatsAppWebhookStatusNormalized", StringComparison.Ordinal));
        _eventPublisher.PublishedOutcomes.Should().ContainSingle(
            "an Accepted outcome must be durably published before the controller returns 2xx (mandate §10/§11)");
    }

    [Fact]
    public async Task An_unknown_route_status_payload_still_returns_200_and_is_audited_as_unknown()
    {
        var body = BuildStatusPayload("some-other-unregistered-phone-id", "wamid.ABC", "sent", "1750030073");
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an unknown route is a permanent classification, never a reason to make Meta retry");
        _loggerProvider.Messages.Should().Contain(m => m.Contains("WhatsAppWebhookRouteUnknown", StringComparison.Ordinal));
        _eventPublisher.PublishedOutcomes.Should().BeEmpty("only Accepted outcomes are ever published");
    }

    [Fact]
    public async Task A_malformed_status_entry_still_returns_200_and_is_audited_as_ignored()
    {
        var body = "{\"entry\":[{\"changes\":[{\"value\":{" +
            "\"metadata\":{\"phone_number_id\":\"" + KnownPhoneNumberId + "\"}," +
            "\"statuses\":[{\"status\":\"sent\",\"timestamp\":\"1750030073\"}]" + // no "id"
            "}}]}]}";
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _loggerProvider.Messages.Should().Contain(m => m.Contains("WhatsAppWebhookStatusIgnored", StringComparison.Ordinal));
        _eventPublisher.PublishedOutcomes.Should().BeEmpty("only Accepted outcomes are ever published");
    }

    [Fact]
    public async Task An_invalid_signature_never_calls_the_route_resolver()
    {
        var body = BuildStatusPayload(KnownPhoneNumberId, "wamid.ABC", "sent", "1750030073");
        using var request = BuildSignedRequest(body, "sha256=" + new string('0', 64));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _routeResolver.WasCalled.Should().BeFalse(
            "route resolution — and therefore any tenant-adjacent lookup — must never happen before the signature is verified");
        _eventPublisher.PublishedOutcomes.Should().BeEmpty();
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

    private sealed class SpyTenantRouteResolver : IWhatsAppTenantRouteResolver
    {
        public bool WasCalled { get; private set; }

        public Task<Guid?> ResolveTenantIdAsync(string phoneNumberId, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(phoneNumberId == KnownPhoneNumberId ? (Guid?)KnownTenantId : null);
        }
    }

    private sealed class SpyStatusEventPublisher : IWhatsAppWebhookStatusEventPublisher
    {
        public List<WebhookStatusProcessingOutcome> PublishedOutcomes { get; } = [];

        public Task PublishAsync(WebhookStatusProcessingOutcome outcome, CancellationToken cancellationToken)
        {
            lock (PublishedOutcomes)
                PublishedOutcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

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

    private sealed class NoOpMessageProcessor : IWhatsAppWebhookMessageProcessor
    {
        public Task<IReadOnlyList<WebhookMessageProcessingOutcome>> ProcessAsync(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WebhookMessageProcessingOutcome>>([]);
    }

    private sealed class NoOpMessageEventPublisher : IWhatsAppWebhookMessageEventPublisher
    {
        public Task PublishAsync(WebhookMessageProcessingOutcome outcome, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
