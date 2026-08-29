using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.ExternalIntegrations.Api.Controllers;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 2.3.3 mandate §32: sends a REALISTIC Meta status
/// payload — including fields <c>MetaWebhookEnvelope</c> deliberately never
/// parses (recipient id, conversation/pricing metadata, a full textual error
/// object) — through the real controller → real signature/route/
/// normalization pipeline → the real <see cref="WhatsAppWebhookStatusEventPublisher"/>,
/// and proves the actual published <see cref="WhatsAppMessageStatusChanged"/>
/// contains none of those sentinels. Only the outbox persistence step itself
/// is faked (no real Wolverine/Postgres needed here — that durability is
/// proven separately by <c>WhatsAppMessageStatusChangedWorkerRoundTripTests</c>).
/// </summary>
public sealed class WhatsAppWebhookStatusEventPiiSafetyHttpTests : IAsyncDisposable
{
    private const string AppSecret = "test-app-secret";
    private const string VerifyToken = "test-verify-token";
    private const string KnownPhoneNumberId = "known-phone-id";
    private const string RecipientSentinel = "5511999998888";
    private const string ConversationSentinel = "CONVERSATION_ID_SENTINEL_DO_NOT_LEAK";
    private const string ErrorTitleSentinel = "ERROR_TITLE_SENTINEL_DO_NOT_LEAK";
    private const string ErrorMessageSentinel = "ERROR_MESSAGE_SENTINEL_DO_NOT_LEAK";
    private static readonly Guid KnownTenantId = Guid.NewGuid();

    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly RecordingIntegrationEventCollector _collector = new();

    public WhatsAppWebhookStatusEventPiiSafetyHttpTests()
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

                    // The REAL publisher — its own fresh-scope-per-outcome
                    // design (see its own doc comment) needs these resolvable
                    // from any child scope, exactly like the real host.
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<IExternalIntegrationsTransactionExecutor, PassThroughTransactionExecutor>();
                    services.AddSingleton<IIntegrationEventCollector>(_collector);
                    services.AddScoped<IWhatsAppWebhookStatusEventPublisher, WhatsAppWebhookStatusEventPublisher>();
                    // Fase 11, Checkpoint 1: required controller dependencies, never exercised by
                    // this file's own statuses[]-only payloads — see NoOpMessageProcessor's own doc comment.
                    services.AddSingleton<IWhatsAppWebhookMessageProcessor>(new NoOpMessageProcessor());
                    services.AddSingleton<IWhatsAppWebhookMessageEventPublisher>(new NoOpMessageEventPublisher());
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
    public async Task The_published_event_never_contains_recipient_conversation_or_full_error_text_sentinels()
    {
        var body = "{\"entry\":[{\"changes\":[{\"value\":{" +
            "\"messaging_product\":\"whatsapp\"," +
            "\"metadata\":{\"phone_number_id\":\"" + KnownPhoneNumberId + "\",\"display_phone_number\":\"15550001111\"}," +
            "\"statuses\":[{" +
                "\"id\":\"wamid.ABC\"," +
                "\"status\":\"failed\"," +
                "\"timestamp\":\"1750030073\"," +
                "\"recipient_id\":\"" + RecipientSentinel + "\"," +
                "\"conversation\":{\"id\":\"" + ConversationSentinel + "\",\"origin\":{\"type\":\"utility\"}}," +
                "\"pricing\":{\"billable\":true,\"pricing_model\":\"CBP\",\"category\":\"utility\"}," +
                "\"errors\":[{\"code\":131026,\"title\":\"" + ErrorTitleSentinel + "\",\"message\":\"" + ErrorMessageSentinel + "\",\"error_data\":{\"details\":\"" + ErrorMessageSentinel + "\"}}]" +
            "}]}}]}]}";
        using var request = BuildSignedRequest(body, ComputeSignature(body, AppSecret));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var published = _collector.Enqueued.Should().ContainSingle().Which.Should().BeOfType<WhatsAppMessageStatusChanged>().Subject;

        published.ProviderMessageId.Should().Be("wamid.ABC");
        published.Status.Should().Be(WhatsAppMessageProviderStatus.Failed);
        published.ProviderErrorCode.Should().Be(131026, "only the numeric error code is ever extracted");

        var serialized = System.Text.Json.JsonSerializer.Serialize(published);
        serialized.Should().NotContain(RecipientSentinel)
            .And.NotContain(ConversationSentinel)
            .And.NotContain(ErrorTitleSentinel)
            .And.NotContain(ErrorMessageSentinel)
            .And.NotContain(KnownPhoneNumberId, "PhoneNumberId itself is routing-only, never carried into the published event");
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

    private sealed class FixedTenantRouteResolver(string knownPhoneNumberId, Guid knownTenantId) : IWhatsAppTenantRouteResolver
    {
        public Task<Guid?> ResolveTenantIdAsync(string phoneNumberId, CancellationToken cancellationToken) =>
            Task.FromResult(phoneNumberId == knownPhoneNumberId ? (Guid?)knownTenantId : null);
    }

    /// <summary>Fase 11, Checkpoint 1 — this file's own scope is statuses[]/status PII-safety only; every payload here carries no messages[], so this is never actually invoked.</summary>
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

    private sealed class PassThroughTransactionExecutor : IExternalIntegrationsTransactionExecutor
    {
        public Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken) =>
            operation();
    }

    private sealed class RecordingIntegrationEventCollector : IIntegrationEventCollector
    {
        public List<IntegrationEvent> Enqueued { get; } = [];

        public void Enqueue(IntegrationEvent @event)
        {
            lock (Enqueued)
                Enqueued.Add(@event);
        }

        public IReadOnlyList<IntegrationEvent> Drain()
        {
            lock (Enqueued)
            {
                var drained = Enqueued.ToArray();
                Enqueued.Clear();
                return drained;
            }
        }
    }
}
