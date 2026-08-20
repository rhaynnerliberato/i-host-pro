using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Api.Controllers;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 2.3.1.1: proves the REAL <c>IHostPro.Api</c> logging
/// configuration (base <c>appsettings.json</c> + <c>appsettings.Development.json</c>,
/// read from their actual files on disk — never a hand-typed copy that could
/// drift) suppresses <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>'s
/// built-in "Request starting"/"Request finished" log lines, which by
/// default include the full request URL — and for the webhook's GET
/// verification handshake, that URL contains <c>hub.verify_token</c>
/// (classified SECRET).
///
/// Root cause (found via a temporary diagnostic, not guessed): the base
/// config already suppressed <c>Microsoft.AspNetCore</c> to Warning in both
/// the standard <c>Logging:LogLevel</c> section and <c>Serilog:MinimumLevel:Override</c>
/// — but <c>appsettings.Development.json</c>'s own Serilog override reopened
/// <c>Microsoft.AspNetCore</c> to Information, and <c>IHostPro.Api</c> uses
/// Serilog (<c>UseSerilog(...).ReadFrom.Configuration(...)</c>) as its actual
/// provider, not the plain <c>Microsoft.Extensions.Logging</c> pipeline — so
/// the real host, in Development specifically, genuinely logged the token.
///
/// Fix: a MORE SPECIFIC override, <c>Microsoft.AspNetCore.Hosting.Diagnostics: Warning</c>,
/// added once to the base <c>appsettings.json</c> only. Serilog's
/// longest-prefix-wins override resolution means this specific override
/// beats Development's broader <c>Microsoft.AspNetCore: Information</c> in
/// every environment automatically — verified here by actually loading both
/// real files and building a real Serilog pipeline from them, exactly as
/// <c>Program.cs</c> does, then sending a real HTTP request through a real
/// TestServer and inspecting every event Serilog's own filtering allowed
/// through. Never uses <c>WebApplicationFactory&lt;IHostPro.Api.Program&gt;</c>
/// (would require the full Postgres/RabbitMQ Testcontainers rig this
/// logging-configuration concern has no dependency on).
/// </summary>
public sealed class WhatsAppWebhookHostLoggingTests : IAsyncDisposable
{
    private const string AppSecret = "test-app-secret";

    // The configured token IS the sentinel — this test class proves the
    // real, successful (200) handshake path doesn't leak, not a rejection.
    private const string VerifyToken = "VERIFY_TOKEN_SENTINEL_DO_NOT_LOG";

    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly CapturingSink _sink = new();
    private readonly Serilog.ILogger _logger;

    public WhatsAppWebhookHostLoggingTests()
    {
        var configuration = LoadRealApiConfiguration();

        // An explicit logger instance passed directly to UseSerilog — never
        // the static Serilog.Log.Logger — so this test cannot race with any
        // other test class that might also configure Serilog.
        _logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.Sink(_sink)
            .CreateLogger();

        var hostBuilder = new HostBuilder()
            .UseSerilog(_logger, dispose: true)
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
                });
                webHost.Configure(app =>
                {
                    app.UseSerilogRequestLogging(); // mirrors Program.cs exactly.
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
        _host.Dispose(); // disposes the Serilog logger too (UseSerilog(logger, dispose: true) above).
    }

    [Fact]
    public async Task GET_verification_with_a_sentinel_token_never_reaches_any_Serilog_sink()
    {
        var response = await _client.GetAsync(
            $"/api/v1/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=x");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request itself must still succeed — only its logging changed");

        var occurrences = _sink.Events.Count(e => e.RenderMessage().Contains(VerifyToken, StringComparison.Ordinal));
        occurrences.Should().Be(0,
            "the real IHostPro.Api Serilog configuration (base + Development, as loaded from the actual files) must " +
            "suppress Microsoft.AspNetCore.Hosting.Diagnostics enough to keep the verify_token out of every sink");
    }

    [Fact]
    public async Task POST_secrets_never_reach_any_Serilog_sink()
    {
        const string appSecretSentinel = "APP_SECRET_SENTINEL_DO_NOT_LOG";
        const string rawBodySentinel = "RAW_BODY_SENTINEL_DO_NOT_LOG";

        var body = $"{{\"marker\":\"{rawBodySentinel}\"}}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecretSentinel));
        var signature = "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);

        await _client.SendAsync(request);

        var rendered = _sink.Events.Select(e => e.RenderMessage()).ToList();
        rendered.Should().NotContain(m => m.Contains(appSecretSentinel, StringComparison.Ordinal));
        rendered.Should().NotContain(m => m.Contains(rawBodySentinel, StringComparison.Ordinal));
        rendered.Should().NotContain(m => m.Contains(signature, StringComparison.Ordinal));
    }

    /// <summary>
    /// Observability was NOT globally silenced by the fix (mandate §6):
    /// Warning-and-above events on the very same category this fix targets
    /// must still reach the sink — proves the override is a level threshold,
    /// not a full category shutdown.
    /// </summary>
    [Fact]
    public void Warning_level_events_on_the_suppressed_category_still_reach_the_sink()
    {
        var probeLogger = _logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Hosting.Diagnostics");

        probeLogger.Warning("Synthetic warning-level probe {ProbeId}", "log-safety-probe");

        _sink.Events.Should().Contain(e => e.RenderMessage().Contains("log-safety-probe", StringComparison.Ordinal),
            "the fix must only raise the MINIMUM level for this category, never silence it entirely");
    }

    /// <summary>The webhook controller's own structured audit lines are unaffected by the host-logging fix.</summary>
    [Fact]
    public async Task The_controllers_own_audit_events_still_reach_the_sink()
    {
        await _client.GetAsync(
            $"/api/v1/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=x");

        _sink.Events.Should().Contain(e => e.RenderMessage().Contains("WhatsAppWebhookVerificationSucceeded", StringComparison.Ordinal));
    }

    private static IConfiguration LoadRealApiConfiguration()
    {
        var apiProjectDirectory = Path.Combine(FindSolutionRoot(), "src", "Host", "IHostPro.Api");

        return new ConfigurationBuilder()
            .SetBasePath(apiProjectDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");
    }

    private sealed class FakeWebhookCredentialProvider(string appSecret, string verifyToken) : IWhatsAppWebhookCredentialProvider
    {
        public Task<string?> GetAppSecretAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(appSecret);
        public Task<string?> GetVerifyTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(verifyToken);
    }

    /// <summary>Captures every Serilog event that survives MinimumLevel/Override filtering — i.e., what would actually reach a real sink.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];
        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }
}
