using System.Diagnostics;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.Contexts.Dashboard.Infrastructure;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.Contexts.Workflow.Infrastructure;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Cross-phase corrective investigation (ADR-020 spike): structural, runtime-
/// model proof of Wolverine's default handler-combining behaviour for a
/// message CLR type with multiple, independently-registered handler classes
/// across bounded contexts — <c>ReservationCreated</c> on <b>master</b> (three
/// real, already-published consumers: Housekeeping, Dashboard, Workflow;
/// Fase 9/Communication's fourth consumer does not exist on this branch).
///
/// Deliberately NOT a duplicate-key-exception-based test: per the corrective
/// mandate, the regression must not rely solely on database side effects as
/// indirect evidence. Instead this inspects Wolverine's own
/// <see cref="HandlerGraph"/> (obtained via the real, public
/// <c>host.GetRuntime()</c> extension, <c>Wolverine.Tracking</c> namespace)
/// directly: <see cref="HandlerGraph.ChainFor(Type)"/> and
/// <see cref="HandlerGraph.AllChains"/> reveal exactly which handler methods
/// Wolverine grouped into which chain, entirely independent of whether any
/// message was ever published or a database write ever attempted.
///
/// The in-process host below is a deliberately minimal subset of
/// IHostPro.Worker's real Program.cs composition root — only the three
/// modules/queues relevant to <c>ReservationCreated</c> (Housekeeping,
/// Dashboard, Workflow) — never a full duplicate of Program.cs. This is a
/// diagnostic/regression test, not production wiring; drift risk is
/// contained by keeping the assertions structural (which handler chains
/// exist) rather than behavioural (this file makes no claim about the full
/// Worker's runtime behaviour beyond this one message type).
///
/// This deliberately does NOT follow <see cref="WolverineThreeStoreCompositionTests"/>'s
/// own documented rule ("never reproduce manually a partial service list —
/// use the real Program.cs composition root"): that rule is scoped to
/// verifying BEHAVIOURAL regressions across the full DI graph (its own
/// stated purpose). This test verifies Wolverine's own compiled
/// <see cref="HandlerGraph"/> STRUCTURE for one message type, the same
/// diagnostic category as that same test class's own
/// <c>CheckExchangesExistAsync</c>/<c>ProvisionMessageStoreSchemaAsync</c>
/// helpers, which likewise build minimal, purpose-built hosts rather than
/// the full composition root.
/// </summary>
public sealed class WolverineHandlerChainIsolationBaselineTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
    private string _migratorConnectionString = null!;
    private string _appConnectionString = null!;

    public async Task InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase("ihostpro_test")
            .WithUsername("ihostpro")
            .WithPassword("ihostpro_dev")
            .Build();

        _rabbitMqContainer = new RabbitMqBuilder()
            .WithImage("rabbitmq:3-management-alpine")
            .WithPortBinding(5672, 5672)
            .Build();

        await Task.WhenAll(_postgresContainer.StartAsync(), _rabbitMqContainer.StartAsync());

        var adminConnectionString = _postgresContainer.GetConnectionString();
        await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var command = adminConnection.CreateCommand();
            command.CommandText = $"""
                CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
        _migratorConnectionString = builder.ConnectionString;
        builder.Username = "ihostpro_app";
        builder.Password = AppRolePassword;
        _appConnectionString = builder.ConnectionString;

        var (exitCode, output) = await RunMigrationRunnerAsync();
        if (exitCode != 0)
            throw new InvalidOperationException($"MigrationRunner failed with exit code {exitCode}. Output:\n{output}");
    }

    public async Task DisposeAsync()
    {
        await _rabbitMqContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

    private async Task<(int ExitCode, string Output)> RunMigrationRunnerAsync()
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "tools", "IHostPro.MigrationRunner", "bin", "Release", "net10.0", "IHostPro.MigrationRunner.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"MigrationRunner build output not found at {dllPath}. Build IHostPro.MigrationRunner in Release configuration first.");

        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__Identity"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Dashboard"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Communication"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Platform"] = _migratorConnectionString;
        psi.Environment["RabbitMq__Host"] = _rabbitMqContainer.Hostname;
        psi.Environment["RabbitMq__VirtualHost"] = "/";
        psi.Environment["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername;
        psi.Environment["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start MigrationRunner process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdoutTask + await stderrTask;

        return (process.ExitCode, output);
    }

    private async Task<IHost> BuildMinimalWorkerSubsetHostAsync(bool applyStickyHandlers = false)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Housekeeping"] = _appConnectionString,
            ["ConnectionStrings:Dashboard"] = _appConnectionString,
            ["ConnectionStrings:Reservations"] = _appConnectionString,
            ["ConnectionStrings:Platform"] = _appConnectionString,
            ["RabbitMq:Host"] = _rabbitMqContainer.Hostname,
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = RabbitMqBuilder.DefaultUsername,
            ["RabbitMq:Password"] = RabbitMqBuilder.DefaultPassword,
        });

        hostBuilder.Services.AddHousekeepingModule(hostBuilder.Configuration);
        hostBuilder.Services.AddDashboardModule(hostBuilder.Configuration);
        hostBuilder.Services.AddDashboardProjectionConsumer();
        hostBuilder.Services.AddWorkflowModule();
        hostBuilder.Services.AddReservationsModule(hostBuilder.Configuration);
        hostBuilder.Services.AddReservationsScheduleProjectionConsumer();

        var platformMessagingConnectionString = hostBuilder.Configuration.GetConnectionString("Platform")!;

        hostBuilder.UseWolverine(opts =>
        {
            opts.UseIHostProRabbitMq(hostBuilder.Configuration, listen: true);

            opts.PersistMessagesWithPostgresql(platformMessagingConnectionString, "platform_messaging");
            opts.EnrollAncillaryPostgresqlOutbox(hostBuilder.Configuration.GetConnectionString("Housekeeping")!, "housekeeping_messaging", typeof(HousekeepingDbContext));
            opts.EnrollAncillaryPostgresqlOutbox(hostBuilder.Configuration.GetConnectionString("Dashboard")!, "dashboard_messaging", typeof(DashboardDbContext));
            opts.EnrollAncillaryPostgresqlOutbox(hostBuilder.Configuration.GetConnectionString("Reservations")!, "reservations_messaging", typeof(ReservationsDbContext));
            opts.UseEntityFrameworkCoreTransactions();
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;

            opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Housekeeping.Application.IHousekeepingMessageExecutionScope>();
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Dashboard.Application.IDashboardMessageExecutionScope>();
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IHostPro.Contexts.Reservations.Application.IReservationsMessageExecutionScope>();

            // Exactly the three real, already-published ReservationCreated
            // consumers on master — mirrors Program.cs's own three
            // Discovery.IncludeAssembly + ListenToRabbitQueue calls for this
            // event, deliberately omitting every other module/queue not
            // relevant to this one message type.
            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Messaging.ReservationCreatedHandler).Assembly);
            var housekeepingListener = opts.ListenToRabbitQueue("housekeeping.reservation-projection");

            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.ReservationCreatedHandler).Assembly);
            var dashboardListener = opts.ListenToRabbitQueue("dashboard.reservation-projection");

            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly);
            var workflowListener = opts.ListenToRabbitQueue("workflow.reservation-created-trigger");

            // ADR-020 spike, Candidate A: endpoint-specific sticky handler
            // mapping (Wolverine 6.22.0's own real, confirmed-by-reflection
            // AddStickyHandler(Type) fluent API on IListenerConfiguration<T>,
            // backed by Endpoint.StickyHandlers) — each queue is told
            // explicitly which single handler TYPE it owns, overriding
            // Wolverine's default same-CLR-type combining for exactly these
            // three endpoints. No topology change: same queues, same
            // exchange bindings, same routing keys — MigrationRunner-owned
            // physical topology is untouched.
            if (applyStickyHandlers)
            {
                housekeepingListener.AddStickyHandler(typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Messaging.ReservationCreatedHandler));
                dashboardListener.AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.ReservationCreatedHandler));
                workflowListener.AddStickyHandler(typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler));
            }

            // PropertyCreated fan-out (Housekeeping + Dashboard) — same
            // ADR-020 category, used by the Property/Cleaning fan-out gates
            // below.
            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Messaging.PropertyCreatedHandler).Assembly);
            var housekeepingPropertyListener = opts.ListenToRabbitQueue("housekeeping.property-projection");

            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.PropertyCreatedHandler).Assembly);
            var dashboardPropertyListener = opts.ListenToRabbitQueue("dashboard.property-projection");

            if (applyStickyHandlers)
            {
                housekeepingPropertyListener.AddStickyHandler(typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Messaging.PropertyCreatedHandler));
                dashboardPropertyListener.AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.PropertyCreatedHandler));
            }

            // CleaningCreated fan-out (Reservations/Agenda + Dashboard).
            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Reservations.Infrastructure.Messaging.CleaningCreatedHandler).Assembly);
            var reservationsCleaningListener = opts.ListenToRabbitQueue("reservations.cleaning-schedule-projection");

            opts.Discovery.IncludeAssembly(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningCreatedHandler).Assembly);
            var dashboardCleaningListener = opts.ListenToRabbitQueue("dashboard.cleaning-projection");

            if (applyStickyHandlers)
            {
                reservationsCleaningListener.AddStickyHandler(typeof(IHostPro.Contexts.Reservations.Infrastructure.Messaging.CleaningCreatedHandler));
                dashboardCleaningListener.AddStickyHandler(typeof(IHostPro.Contexts.Dashboard.Infrastructure.Messaging.CleaningCreatedHandler));
            }
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Baseline_master_combines_the_three_ReservationCreated_handlers_into_a_single_chain()
    {
        using var host = await BuildMinimalWorkerSubsetHostAsync();
        try
        {
            var runtime = host.GetRuntime();

            var chainsForType = runtime.Handlers.AllChains()
                .Where(c => c.MessageType == typeof(ReservationCreated))
                .ToList();

            // Structural evidence #1: exactly ONE chain exists for
            // ReservationCreated across the whole process, even though three
            // independent bounded contexts each declared their own handler —
            // Wolverine's default MultipleHandlerBehavior combined them
            // instead of keeping them separate per listening endpoint.
            chainsForType.Should().ContainSingle(
                "Wolverine's default behaviour combines every handler discovered for the same CLR message type " +
                "into a single HandlerChain, regardless of how many distinct queues/listeners are configured for it");

            var combinedChain = chainsForType[0];
            var handlerCalls = combinedChain.HandlerCalls();

            // Structural evidence #2: that single chain's own handler-call
            // list contains all three bounded contexts' Handle methods —
            // this is Wolverine's own compiled model, not an inference from
            // a database exception or any message ever having been
            // processed.
            handlerCalls.Should().HaveCount(3,
                "the combined chain must contain Housekeeping's, Dashboard's and Workflow's own ReservationCreatedHandler.Handle " +
                "calls together — direct proof of the handler-chain-combining defect");

            var handlerTypeNames = handlerCalls.Select(c => c.HandlerType.FullName).ToList();
            handlerTypeNames.Should().Contain(t => t!.Contains("Housekeeping"));
            handlerTypeNames.Should().Contain(t => t!.Contains("Dashboard"));
            handlerTypeNames.Should().Contain(t => t!.Contains("Workflow"));

            // Same structural proof, generalized to the other two fan-out
            // categories in the ADR-020 inventory: Property (Housekeeping +
            // Dashboard) and Cleaning (Reservations/Agenda + Dashboard).
            AssertCombinedChain(runtime, typeof(PropertyCreated), 2, "Housekeeping", "Dashboard");
            AssertCombinedChain(runtime, typeof(CleaningCreated), 2, "Reservations", "Dashboard");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static void AssertCombinedChain(WolverineRuntime runtime, Type messageType, int expectedHandlerCount, params string[] expectedNamespaceFragments)
    {
        var chainsForType = runtime.Handlers.AllChains().Where(c => c.MessageType == messageType).ToList();
        chainsForType.Should().ContainSingle(
            $"{messageType.Name} has {expectedHandlerCount} independent consumers in this process and, without a fix, " +
            "Wolverine combines them into a single chain regardless of endpoint");

        var handlerCalls = chainsForType[0].HandlerCalls();
        handlerCalls.Should().HaveCount(expectedHandlerCount,
            $"the combined {messageType.Name} chain must contain all {expectedHandlerCount} bounded contexts' Handle calls together");

        var handlerTypeNames = handlerCalls.Select(c => c.HandlerType.FullName).ToList();
        foreach (var fragment in expectedNamespaceFragments)
            handlerTypeNames.Should().Contain(t => t!.Contains(fragment));
    }

    private static void AssertIsolatedChains(WolverineRuntime runtime, Type messageType, int expectedChainCount, string[] queueNameFragments, string[] namespaceFragments)
    {
        var chainsForType = runtime.Handlers.AllChains().Where(c => c.MessageType == messageType).ToList();
        chainsForType.Should().HaveCount(expectedChainCount,
            $"AddStickyHandler must split {messageType.Name}'s combined chain back into one independent chain per endpoint");

        var totalHandlerCalls = 0;
        foreach (var chain in chainsForType)
        {
            var calls = chain.HandlerCalls();
            calls.Should().ContainSingle($"each sticky-isolated {messageType.Name} chain must own exactly one handler");
            totalHandlerCalls += calls.Length;
        }

        totalHandlerCalls.Should().Be(expectedChainCount,
            $"{messageType.Name} must yield exactly {expectedChainCount} logical handler executions total, never fewer (lost handler) " +
            "and never more (N x M fan-out from a botched sticky mapping)");

        for (var i = 0; i < queueNameFragments.Length; i++)
        {
            var queueFragment = queueNameFragments[i];
            var namespaceFragment = namespaceFragments[i];
            var chain = chainsForType.Single(c => c.HandlerCalls()[0].HandlerType.FullName!.Contains(namespaceFragment));
            chain.Endpoints.Should().ContainSingle(e => e.Uri.ToString().Contains(queueFragment));
        }
    }

    [Fact]
    public async Task Fix_AddStickyHandler_isolates_ReservationCreated_Property_and_Cleaning_handlers_into_separate_chains()
    {
        using var host = await BuildMinimalWorkerSubsetHostAsync(applyStickyHandlers: true);
        try
        {
            var runtime = host.GetRuntime();

            // Structural evidence, generalized across the three ADR-020
            // fan-out categories exercised by this host: with
            // AddStickyHandler applied, Wolverine no longer combines the
            // handlers into one shared chain — it keeps exactly one
            // independent chain per sticky-bound endpoint, each with
            // exactly one handler call, total calls == queue count (never
            // fewer — lost handler; never more — N x M fan-out).
            AssertIsolatedChains(
                runtime, typeof(ReservationCreated), expectedChainCount: 3,
                queueNameFragments: ["housekeeping.reservation-projection", "dashboard.reservation-projection", "workflow.reservation-created-trigger"],
                namespaceFragments: ["Housekeeping", "Dashboard", "Workflow"]);

            AssertIsolatedChains(
                runtime, typeof(PropertyCreated), expectedChainCount: 2,
                queueNameFragments: ["housekeeping.property-projection", "dashboard.property-projection"],
                namespaceFragments: ["Housekeeping", "Dashboard"]);

            AssertIsolatedChains(
                runtime, typeof(CleaningCreated), expectedChainCount: 2,
                queueNameFragments: ["reservations.cleaning-schedule-projection", "dashboard.cleaning-projection"],
                namespaceFragments: ["Reservations", "Dashboard"]);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");
    }
}
