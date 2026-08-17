using FluentAssertions;
using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Dashboard.Infrastructure.Messaging;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces ADR-016 (third application, Fase 7, Incremento 2 — Dashboard
/// &amp; Reporting Foundation, Checkpoint 1), generalizing ADR-015's
/// Housekeeping finding and ADR-016's own Reservations application to
/// Dashboard. <c>DashboardMessageExecutionScope</c> is the single,
/// deliberately-authorized boundary holding <see cref="IServiceScopeFactory"/>
/// — every one of the eighteen thin Wolverine adapters must keep depending
/// only on the ordinary constructor-injected graph. Mirrors
/// <c>HousekeepingMessageExecutionScopeArchitectureTests</c>/
/// <c>ReservationsMessageExecutionScopeArchitectureTests</c> exactly.
/// </summary>
public class DashboardMessageExecutionScopeArchitectureTests
{
    private static readonly Type[] DashboardAssemblyAnchors =
    [
        typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference),
        typeof(IHostPro.Contexts.Dashboard.Contracts.AssemblyReference),
        typeof(IDashboardMessageExecutionScope),
        typeof(DashboardMessageExecutionScope),
    ];

    [Fact]
    public void Only_DashboardMessageExecutionScope_May_Depend_On_IServiceScopeFactory()
    {
        var typesDependingOnScopeFactory = DashboardAssemblyAnchors
            .Select(anchor => anchor.Assembly)
            .Distinct()
            .SelectMany(assembly => Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn("Microsoft.Extensions.DependencyInjection.IServiceScopeFactory")
                .GetTypes())
            .Distinct()
            .ToList();

        typesDependingOnScopeFactory.Should().ContainSingle()
            .Which.Should().Be(typeof(DashboardMessageExecutionScope),
                "IDashboardMessageExecutionScope's own implementation is the single, deliberately-authorized " +
                "holder of IServiceScopeFactory in Dashboard (ADR-016) — any other match means a new class " +
                "started resolving its own child scope outside the approved boundary.");
    }

    [Fact]
    public void Wolverine_Adapters_Never_Depend_On_DashboardDbContext_Or_TransactionExecutor_Or_Synchronizers_Or_ServiceScopeFactory()
    {
        // The eighteen thin Wolverine entrypoints (ReservationCreatedHandler,
        // ReservationUpdatedHandler, ReservationCancelledHandler,
        // CleaningCreatedHandler and its nine siblings,
        // the four PropertyXxxHandler, CleaningOccurrenceRegisteredHandler)
        // may depend only on the message type, MessageContext,
        // IDashboardMessageExecutionScope and CancellationToken — never
        // directly on persistence/transaction/scope-factory/synchronizer
        // types Wolverine's own codegen would otherwise try to inline.
        var adapterTypes = Types.InAssembly(typeof(DashboardMessageExecutionScope).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.Dashboard.Infrastructure.Messaging")
            .And()
            .DoNotHaveName(nameof(DashboardMessageExecutionScope))
            .GetTypes();

        adapterTypes.Should().HaveCount(18, "eighteen Wolverine adapter classes are expected in this namespace " +
            "(3 Reservation + 10 Cleaning + 4 Property + 1 Occurrence)");

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext",
            "IHostPro.Contexts.Dashboard.Application.IDashboardTransactionExecutor",
            "IHostPro.Contexts.Dashboard.Infrastructure.Projections.DashboardReservationProjectionSynchronizer",
            "IHostPro.Contexts.Dashboard.Infrastructure.Projections.DashboardCleaningProjectionSynchronizer",
            "IHostPro.Contexts.Dashboard.Infrastructure.Projections.DashboardPropertyProjectionSynchronizer",
            "IHostPro.Contexts.Dashboard.Infrastructure.Projections.DashboardOccurrenceProjectionSynchronizer",
            "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
            "Wolverine.EntityFrameworkCore.IDbContextOutbox",
        };

        foreach (var adapterType in adapterTypes)
        {
            var result = Types.InAssembly(adapterType.Assembly)
                .That()
                .HaveName(adapterType.Name)
                .Should()
                .NotHaveDependencyOnAny(forbiddenDependencies)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"{adapterType.FullName} must depend only on IDashboardMessageExecutionScope for anything Dashboard-persistence-related (ADR-016)");
        }
    }
}
