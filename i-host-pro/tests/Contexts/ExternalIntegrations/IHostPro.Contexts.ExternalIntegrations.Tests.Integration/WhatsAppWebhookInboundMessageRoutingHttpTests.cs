using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Api.Controllers;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 1 (Inbound Conversation Foundation) mandate item 37:
/// proves the real HTTP pipeline for the new <c>messages[]</c> ingestion
/// slice added on top of the pre-existing <c>statuses[]</c> path — a known
/// route text message is accepted/normalized, an unknown route is a safe
/// no-op, an invalid signature never reaches routing, and a single delivery
/// mixing both <c>statuses[]</c> and <c>messages[]</c> processes both without
/// either stealing or duplicating the other's entries. Mirrors
/// <c>WhatsAppWebhookStatusRoutingHttpTests</c> exactly.
/// </summary>
public sealed class WhatsAppWebhookInboundMessageRoutingHttpTests : IAsyncDisposable
{
    private const string AppSecret = "test-app-secret";
    private const string VerifyToken = "test-verify-token";
    private const string KnownPhoneNumberId = "known-phone-id";
    private static readonly Guid KnownTenantId = Guid.NewGuid();

    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly ListLoggerProvider _loggerProvider = new();
    private readonly SpyTenantRouteResolver _routeResolver = new();
    private readonly SpyMessageEventPublisher _messageEventPublisher = new();
    private readonly SpyStatusEventPublisher _statusEventPublisher = new();

    public WhatsAppWebhookInboundMessageRoutingHttpTests()
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
                    services.AddSingleton<IWhatsAppWebhookStatusEventPublisher>(_statusEventPublisher);
                    services.AddScoped<IWhatsAppWebhookMessageProcessor, MetaWebhookMessageProcessor>();
                    services.AddSingleton<IWhatsAppWebhookMessageEventPublisher>(_messageEventPublisher);
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
    public async Task A_known_route_text_message_is_accepted_and_normalized()
    {
        var body = BuildMessagePayload(KnownPhoneNumberId, "wamid.INBOUND1", "5511999998888", "text", "Olá, preciso de ajuda", "1750030073");
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _loggerProvider.Messages.Should().Contain(m => m.Contains("WhatsAppWebhookMessageNormalized", StringComparison.Ordinal));
        _messageEventPublisher.PublishedOutcomes.Should().ContainSingle(
            "an Accepted outcome must be durably published before the controller returns 2xx");
        var published = _messageEventPublisher.PublishedOutcomes[0];
        published.TenantId.Should().Be(KnownTenantId);
        published.SenderPhoneNormalized.Should().Be("5511999998888");
        published.MessageType.Should().Be(InboundGuestMessageType.Text);
        published.Text.Should().Be("Olá, preciso de ajuda");
    }

    [Fact]
    public async Task An_unsupported_message_type_is_accepted_with_no_text()
    {
        var body = BuildMessagePayload(KnownPhoneNumberId, "wamid.INBOUND2", "5511999998888", "image", body: null, timestamp: "1750030073");
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _messageEventPublisher.PublishedOutcomes.Should().ContainSingle();
        var published = _messageEventPublisher.PublishedOutcomes[0];
        published.MessageType.Should().Be(InboundGuestMessageType.Unsupported);
        published.Text.Should().BeNull("CP1 is TEXT ONLY — no media payload is ever downloaded/modeled");
    }

    [Fact]
    public async Task An_unknown_route_message_still_returns_200_and_is_audited_as_unknown()
    {
        var body = BuildMessagePayload("some-other-unregistered-phone-id", "wamid.INBOUND3", "5511999998888", "text", "oi", "1750030073");
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an unknown route is a permanent classification, never a reason to make Meta retry");
        _loggerProvider.Messages.Should().Contain(m => m.Contains("WhatsAppWebhookRouteUnknown", StringComparison.Ordinal));
        _messageEventPublisher.PublishedOutcomes.Should().BeEmpty("only Accepted outcomes are ever published");
    }

    [Fact]
    public async Task An_invalid_signature_never_calls_the_route_resolver_for_a_message_payload()
    {
        var body = BuildMessagePayload(KnownPhoneNumberId, "wamid.INBOUND4", "5511999998888", "text", "oi", "1750030073");
        using var request = BuildSignedRequest(body, "sha256=" + new string('0', 64));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _routeResolver.WasCalled.Should().BeFalse(
            "route resolution — and therefore any tenant-adjacent lookup — must never happen before the signature is verified");
        _messageEventPublisher.PublishedOutcomes.Should().BeEmpty();
    }

    /// <summary>Mandate item 37: a single delivery mixing statuses[] and messages[] in different changes processes BOTH, neither stealing nor duplicating the other's entries.</summary>
    [Fact]
    public async Task A_delivery_mixing_statuses_and_messages_processes_both_independently()
    {
        var body = "{\"entry\":[{\"changes\":[" +
            "{\"value\":{\"metadata\":{\"phone_number_id\":\"" + KnownPhoneNumberId + "\"}," +
            "\"statuses\":[{\"id\":\"wamid.STATUS1\",\"status\":\"delivered\",\"timestamp\":\"1750030073\"}]}}," +
            "{\"value\":{\"metadata\":{\"phone_number_id\":\"" + KnownPhoneNumberId + "\"}," +
            "\"messages\":[{\"id\":\"wamid.INBOUND5\",\"from\":\"5511999998888\",\"type\":\"text\",\"timestamp\":\"1750030074\",\"text\":{\"body\":\"oi\"}}]}}" +
            "]}]}";
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _statusEventPublisher.PublishedOutcomes.Should().ContainSingle(o => o.ProviderMessageId == "wamid.STATUS1");
        _messageEventPublisher.PublishedOutcomes.Should().ContainSingle(o => o.ProviderMessageId == "wamid.INBOUND5");
    }

    private static string BuildMessagePayload(
        string phoneNumberId, string id, string from, string type, string? body, string timestamp)
    {
        var textPart = body is null ? "" : ",\"text\":{\"body\":\"" + body + "\"}";
        return "{\"entry\":[{\"changes\":[{\"value\":{" +
            "\"metadata\":{\"phone_number_id\":\"" + phoneNumberId + "\"}," +
            "\"messages\":[{\"id\":\"" + id + "\",\"from\":\"" + from + "\",\"type\":\"" + type + "\",\"timestamp\":\"" + timestamp + "\"" + textPart + "}]" +
            "}}]}]}";
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

    private sealed class SpyTenantRouteResolver : IWhatsAppTenantRouteResolver
    {
        public bool WasCalled { get; private set; }

        public Task<Guid?> ResolveTenantIdAsync(string phoneNumberId, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(phoneNumberId == KnownPhoneNumberId ? (Guid?)KnownTenantId : null);
        }
    }

    private sealed class SpyMessageEventPublisher : IWhatsAppWebhookMessageEventPublisher
    {
        public List<WebhookMessageProcessingOutcome> PublishedOutcomes { get; } = [];

        public Task PublishAsync(WebhookMessageProcessingOutcome outcome, CancellationToken cancellationToken)
        {
            lock (PublishedOutcomes)
                PublishedOutcomes.Add(outcome);
            return Task.CompletedTask;
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
}
