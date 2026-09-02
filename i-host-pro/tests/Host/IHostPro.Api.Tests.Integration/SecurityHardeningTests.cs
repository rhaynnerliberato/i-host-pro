using System.Net;
using FluentAssertions;
using IHostPro.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — focused,
/// deliberately lightweight proofs for the safe-hardening items (mandate
/// §1 A-E) that do NOT need the full Postgres/RabbitMQ/Worker fixture the
/// other files in this project pay for: security headers, the CORS
/// Production fail-fast rule, sanitized unhandled-exception responses, and
/// ForwardedHeaders' "never blindly trust an unconfigured proxy" rule. Each
/// builds only the minimal <c>Microsoft.AspNetCore.TestHost</c> pipeline the
/// scenario actually needs — never the real <c>Program.cs</c> (whose
/// top-level try/catch swallows unhandled exceptions into <c>Log.Fatal</c>,
/// which would make the CORS fail-fast throw unobservable through a real
/// <c>WebApplicationFactory</c> boot).
/// </summary>
public sealed class SecurityHardeningTests
{
    // ---- Security headers -------------------------------------------------

    [Fact]
    public async Task UseIHostProSecurityHeaders_sets_the_expected_headers_and_no_CSP()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseIHostProSecurityHeaders();
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/anything");

        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("strict-origin-when-cross-origin");
        response.Headers.GetValues("Permissions-Policy").Should().Equal("geolocation=(), camera=(), microphone=(), payment=()");
        // Mandate explicit instruction — never add an arbitrary CSP here.
        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
    }

    // ---- CORS Production fail-fast (CorsOriginsResolver) ------------------

    [Fact]
    public void ResolveAllowedOrigins_throws_in_Production_when_unconfigured()
    {
        var configuration = new ConfigurationBuilder().Build();

        var act = () => CorsOriginsResolver.ResolveAllowedOrigins(configuration, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(CorsOriginsResolver.MissingProductionOriginsMessage);
    }

    [Fact]
    public void ResolveAllowedOrigins_throws_in_Production_when_configured_as_an_empty_array()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();

        var act = () => CorsOriginsResolver.ResolveAllowedOrigins(configuration, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(CorsOriginsResolver.MissingProductionOriginsMessage);
    }

    [Fact]
    public void ResolveAllowedOrigins_returns_the_configured_origins_in_Production_when_present()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://app.ihostpro.example",
        }).Build();

        var origins = CorsOriginsResolver.ResolveAllowedOrigins(configuration, isProduction: true);

        origins.Should().Equal("https://app.ihostpro.example");
    }

    [Fact]
    public void ResolveAllowedOrigins_falls_back_to_localhost_outside_Production_when_unconfigured()
    {
        // Development/Test/every non-Production environment — deliberately
        // unchanged from before this checkpoint (mandate §4: "Development
        // pode manter sua configuração local").
        var configuration = new ConfigurationBuilder().Build();

        var origins = CorsOriginsResolver.ResolveAllowedOrigins(configuration, isProduction: false);

        origins.Should().Equal("http://localhost:4200");
    }

    // ---- Sanitized unhandled-exception responses ---------------------------

    [Fact]
    public async Task SanitizedExceptionHandler_never_leaks_the_real_exception_message_or_stack_trace()
    {
        const string SensitiveDetail = "Connection failed: Host=db.internal;Password=super-secret-value-42";

        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddProblemDetails();
                    services.AddExceptionHandler<SanitizedExceptionHandler>();
                    services.AddLogging();
                })
                .Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.Run(_ => throw new InvalidOperationException(SensitiveDetail));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/anything");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        body.Should().NotContain("super-secret-value-42");
        body.Should().NotContain("Password=");
        body.Should().NotContain(nameof(InvalidOperationException));
        body.Should().Contain("traceId");
    }

    // ---- ForwardedHeaders: never trust an unconfigured proxy ----------------

    [Fact]
    public async Task ForwardedHeaders_from_an_untrusted_source_are_ignored_by_default()
    {
        // ASP.NET Core's own safe default (KnownNetworks = 127.0.0.0/8,
        // KnownProxies = ::1) — a spoofed X-Forwarded-For sent by a directly
        // connecting client outside that set must be ignored rather than
        // silently trusted (mandate §3: "sem proxy confiável configurado,
        // não confiar cegamente"). TestServer reports a null
        // Connection.RemoteIpAddress by default, which ForwardedHeadersMiddleware
        // treats as having nothing to check against — a middleware inserted
        // before it stands in for "the actual directly-connecting peer",
        // exactly as Kestrel would populate it behind a real socket.
        var untrustedPeer = System.Net.IPAddress.Parse("198.51.100.1");

        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .Configure(app =>
                {
                    app.Use((ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = untrustedPeer;
                        return next(ctx);
                    });
                    app.UseForwardedHeaders(new ForwardedHeadersOptions
                    {
                        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
                    });
                    app.Run(ctx => ctx.Response.WriteAsync(ctx.Connection.RemoteIpAddress?.ToString() ?? "null"));
                }))
            .StartAsync();

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.99");

        var response = await client.GetAsync("/anything");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Be(untrustedPeer.ToString(), "an untrusted forwarder must never override the observed remote address");
    }

    [Fact]
    public async Task ForwardedHeaders_from_a_configured_trusted_proxy_are_honored()
    {
        // The companion positive case: once a specific proxy address is
        // configured as trusted (exactly what CP5 will do for the real
        // reverse proxy via ForwardedHeaders:KnownProxies), its
        // X-Forwarded-For IS honored — proving the mechanism actually works,
        // not just that it is safe when unconfigured.
        var trustedProxy = System.Net.IPAddress.Parse("10.0.0.5");
        var realClientIp = System.Net.IPAddress.Parse("203.0.113.99");

        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .Configure(app =>
                {
                    app.Use((ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = trustedProxy;
                        return next(ctx);
                    });
                    var options = new ForwardedHeadersOptions
                    {
                        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
                    };
                    options.KnownProxies.Add(trustedProxy);
                    app.UseForwardedHeaders(options);
                    app.Run(ctx => ctx.Response.WriteAsync(ctx.Connection.RemoteIpAddress?.ToString() ?? "null"));
                }))
            .StartAsync();

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", realClientIp.ToString());

        var response = await client.GetAsync("/anything");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Be(realClientIp.ToString(), "a configured trusted proxy's forwarded client address must be honored");
    }
}
