using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Runtime.Handlers;

namespace IHostPro.Api.Tests.Integration;

public sealed record ScopedRetryProbeMessage(int Marker);

public static class ScopedRetryProbeHandler
{
    public static int Attempts;

    /// <summary>Mirrors <c>WhatsAppMessageStatusChangedHandler.Configure</c>'s exact pattern (Fase 9, Checkpoint 2.3.3.1), never the production cooldowns — this test proves type-scoping, not exact timing (already covered by the real E2E tests).</summary>
    public static void Configure(HandlerChain chain) =>
        chain.OnException<WhatsAppMessageNotYetAvailableException>()
            .RetryWithCooldown(50.Milliseconds(), 50.Milliseconds(), 50.Milliseconds());

    public static Task Handle(ScopedRetryProbeMessage msg)
    {
        Interlocked.Increment(ref Attempts);
        throw new WhatsAppMessageNotYetAvailableException("probe: the specific exception this policy targets — must retry.");
    }
}

public sealed record UnrelatedGenericExceptionProbeMessage(int Marker);

public static class UnrelatedGenericExceptionProbeHandler
{
    public static int Attempts;

    // Deliberately NO Configure method — mirrors any other handler in this
    // codebase that never opted into a chain-specific retry policy, and
    // proves ScopedRetryProbeHandler's own policy above never leaks onto an
    // unrelated handler chain.
    public static Task Handle(UnrelatedGenericExceptionProbeMessage msg)
    {
        Interlocked.Increment(ref Attempts);
        throw new InvalidOperationException("probe: a generic, unrelated exception — must NOT receive the specific retry policy.");
    }
}

/// <summary>
/// Fase 9, Checkpoint 2.3.3.1 (second correction): proves — against a real,
/// unmocked Wolverine host, never inspecting internals — that
/// <c>chain.OnException&lt;WhatsAppMessageNotYetAvailableException&gt;().RetryWithCooldown(...)</c>
/// is genuinely scoped by exact exception TYPE, not a blanket policy: the
/// specific exception this checkpoint's missing-Message race throws gets
/// the full bounded-retry treatment, while a generic, unrelated
/// <see cref="InvalidOperationException"/> from a completely different
/// handler chain gets Wolverine's own untouched default (one attempt, no
/// retry — confirmed for this codebase in <c>WhatsAppMessageStatusMissingMessageRetryTests</c>'s
/// own investigation). No RabbitMQ/subprocess needed — Wolverine's retry
/// policy is a transport-agnostic execution-layer concern, confirmed
/// empirically during that same investigation; a real Postgres-backed
/// durable store is enough to exercise the exact same continuation/policy
/// pipeline the real RabbitMQ-hosted consumer uses.
/// </summary>
public sealed class WhatsAppMessageStatusRetryPolicyScopingTests : IAsyncLifetime
{
    private const string Schema = "scoping_probe_messaging";

    private PostgreSqlContainer _postgresContainer = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase("ihostpro_test")
            .WithUsername("ihostpro")
            .WithPassword("ihostpro_dev")
            .Build();
        await _postgresContainer.StartAsync();

        // The container's own superuser connection is used directly —
        // this is a throwaway, single-use, isolated Testcontainer database
        // that nothing else touches, never production infrastructure, so
        // none of the app/migrator role-separation concerns that apply
        // elsewhere in this codebase apply here.
        _connectionString = _postgresContainer.GetConnectionString();
    }

    public async Task DisposeAsync() => await _postgresContainer.DisposeAsync();

    [Fact]
    public async Task The_specific_exception_retries_while_an_unrelated_one_does_not()
    {
        ScopedRetryProbeHandler.Attempts = 0;
        UnrelatedGenericExceptionProbeHandler.Attempts = 0;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType(typeof(ScopedRetryProbeHandler));
                opts.Discovery.IncludeType(typeof(UnrelatedGenericExceptionProbeHandler));
                opts.PersistMessagesWithPostgresql(_connectionString, Schema);
                opts.AutoBuildMessageStorageOnStartup = JasperFx.AutoCreate.CreateOrUpdate;
                opts.UseEntityFrameworkCoreTransactions();
            })
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(new ScopedRetryProbeMessage(1));
        await bus.PublishAsync(new UnrelatedGenericExceptionProbeMessage(1));

        // Comfortably covers this test's own short 50ms/50ms/50ms schedule
        // (~150ms total) plus Wolverine startup/dispatch overhead.
        await Task.Delay(TimeSpan.FromSeconds(5));

        await host.StopAsync();

        ScopedRetryProbeHandler.Attempts.Should().Be(4,
            "WhatsAppMessageNotYetAvailableException is the exact type this policy targets — one initial attempt plus three configured retries");
        UnrelatedGenericExceptionProbeHandler.Attempts.Should().Be(1,
            "a generic, unrelated InvalidOperationException from a completely different handler chain must get Wolverine's own untouched default — never this policy's retry treatment");
    }
}
