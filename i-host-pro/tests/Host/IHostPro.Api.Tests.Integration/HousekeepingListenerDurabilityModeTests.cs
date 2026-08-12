using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using JasperFx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 6, Checkpoint 6 homologação — Section 10 of the user's approved
/// protocol asked to "validate that housekeeping.reservation-projection /
/// housekeeping.property-projection are durable listeners". A real
/// investigation (documented in full in the Fase 6 homologation doc, §10.8)
/// found the opposite of that assumption: with <c>opts.UseEntityFrameworkCoreTransactions()</c>
/// active (required platform-wide by ADR-015's own execution-boundary
/// design), Wolverine configures EVERY RabbitMQ listener — not just
/// Housekeeping's two — as <see cref="EndpointMode.Inline"/>, never
/// <see cref="EndpointMode.Durable"/>. Confirmed empirically two ways: (1) a
/// real end-to-end run against Postgres never produced a single row in
/// <c>housekeeping_messaging.wolverine_incoming_envelopes</c> for an
/// in-flight <c>ReservationCancelled</c>, despite polling continuously
/// through a cold-start codegen window that is known (from
/// <c>ReservationCancelledRedeliveryTests</c>'s own captured Debug-level log)
/// to last several seconds; (2) directly inspecting Wolverine's own public
/// <c>IWolverineRuntime.Options.Transports.AllEndpoints()</c> collection
/// in-process shows <c>Mode=Inline</c> for
/// <c>rabbitmq://queue/housekeeping.reservation-projection</c>,
/// <c>rabbitmq://queue/housekeeping.property-projection</c>, AND
/// <c>rabbitmq://queue/configuration.policy-updated</c> (Fase 5) alike —
/// this is a platform-wide consequence of the global EF Core transactional
/// messaging policy, not something introduced by or unique to Housekeeping.
///
/// This test locks in that REAL, confirmed configuration as a fast,
/// deterministic regression check (no real broker/Postgres needed — it only
/// inspects the composed <see cref="WolverineOptions"/>) — it makes no
/// claim about whether Inline mode is the right design; that judgment is
/// explicitly left to the user (Fase 6 doc §10.8).
/// </summary>
public sealed class HousekeepingListenerDurabilityModeTests
{
    [Fact]
    public void Housekeeping_RabbitMq_listeners_run_in_Inline_mode_not_Durable_a_consequence_of_the_global_EF_Core_transactional_messaging_policy()
    {
        using var host = BuildHost();
        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();

        var reservationProjection = runtime.Options.Transports.AllEndpoints()
            .Single(e => e.Uri == new Uri("rabbitmq://queue/housekeeping.reservation-projection"));
        var propertyProjection = runtime.Options.Transports.AllEndpoints()
            .Single(e => e.Uri == new Uri("rabbitmq://queue/housekeeping.property-projection"));

        reservationProjection.Mode.Should().Be(EndpointMode.Inline,
            "opts.UseEntityFrameworkCoreTransactions() configures every RabbitMQ listener Inline platform-wide — " +
            "there is no separate, Postgres-backed durable-inbox row for this queue; redelivery protection for it " +
            "is domain-level idempotency only, confirmed in ReservationCancelledRedeliveryTests");
        propertyProjection.Mode.Should().Be(EndpointMode.Inline, "same global policy as housekeeping.reservation-projection");
    }

    private static IHost BuildHost()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Housekeeping"] = "Host=localhost;Database=doesnotmatter;Username=x;Password=x",
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddHousekeepingModule(configuration);

        hostBuilder.UseWolverine(opts =>
        {
            // Mirrors IHostPro.Worker/Program.cs's own composition exactly —
            // never a real broker/Postgres connection, since this test only
            // inspects the composed Endpoint objects, never calls
            // host.StartAsync().
            opts.PersistMessagesWithPostgresql("Host=localhost;Database=doesnotmatter;Username=x;Password=x", "platform_messaging_scratch");
            opts.EnrollAncillaryPostgresqlOutbox("Host=localhost;Database=doesnotmatter;Username=x;Password=x", "housekeeping_messaging_scratch", typeof(HousekeepingDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Housekeeping.Application.IHousekeepingMessageExecutionScope>();

            opts.Discovery.IncludeAssembly(typeof(PropertyCreatedHandler).Assembly);
            opts.ListenToRabbitQueue("housekeeping.property-projection");
            opts.ListenToRabbitQueue("housekeeping.reservation-projection");
        });

        return hostBuilder.Build();
    }
}
