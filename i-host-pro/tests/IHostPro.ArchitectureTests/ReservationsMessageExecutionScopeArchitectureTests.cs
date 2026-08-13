using FluentAssertions;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Infrastructure.Messaging;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces ADR-016 (Fase 7, Checkpoint 1 CLOSURE) — Tenant-safe execution
/// boundary for persistent Wolverine consumers, generalizing ADR-015's
/// Housekeeping finding to Reservations. The user approved
/// <c>opts.CodeGeneration.AlwaysUseServiceLocationFor&lt;
/// IReservationsMessageExecutionScope&gt;()</c> exclusively for the single
/// boundary that holds <see cref="IServiceScopeFactory"/> — every other
/// class in Reservations, including the ten thin Wolverine adapters
/// themselves, must keep depending only on the ordinary constructor-injected
/// graph. These tests make that boundary a build-breaking guarantee instead
/// of a comment — mirrors <c>HousekeepingMessageExecutionScopeArchitectureTests</c>
/// exactly.
/// </summary>
public class ReservationsMessageExecutionScopeArchitectureTests
{
    private static readonly Type[] ReservationsAssemblyAnchors =
    [
        typeof(IHostPro.Contexts.Reservations.Domain.Reservation),
        typeof(IHostPro.Contexts.Reservations.Contracts.AssemblyReference),
        typeof(IReservationsMessageExecutionScope),
        typeof(ReservationsMessageExecutionScope),
        typeof(IHostPro.Contexts.Reservations.Api.AssemblyReference),
    ];

    [Fact]
    public void Only_ReservationsMessageExecutionScope_May_Depend_On_IServiceScopeFactory()
    {
        // IServiceScopeFactory is a Singleton — resolving it anywhere else in
        // Reservations would either bypass the tenant-execution boundary
        // ADR-016 establishes, or (per Wolverine's own codegen) risk
        // reproducing the exact InvalidServiceLocationException this
        // boundary already exists to correct. Not "should be rare" — must be
        // exactly one type, in exactly one assembly.
        var typesDependingOnScopeFactory = ReservationsAssemblyAnchors
            .Select(anchor => anchor.Assembly)
            .Distinct()
            .SelectMany(assembly => Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn("Microsoft.Extensions.DependencyInjection.IServiceScopeFactory")
                .GetTypes())
            .Distinct()
            .ToList();

        typesDependingOnScopeFactory.Should().ContainSingle()
            .Which.Should().Be(typeof(ReservationsMessageExecutionScope),
                "IReservationsMessageExecutionScope's own implementation is the single, deliberately-authorized " +
                "holder of IServiceScopeFactory in Reservations (ADR-016) — any other match means a new class " +
                "started resolving its own child scope outside the approved boundary.");
    }

    [Fact]
    public void Cleaning_Wolverine_Adapters_Never_Depend_On_ReservationsDbContext_Or_TransactionExecutor_Or_ServiceScopeFactory()
    {
        // The ten thin Wolverine entrypoints (CleaningCreatedHandler,
        // CleaningAssignedHandler, CleaningInTransitHandler,
        // CleaningStartedHandler, CleaningInspectionStartedHandler,
        // CleaningCompletedHandler, CleaningInterruptedHandler,
        // CleaningNeedsHelpHandler, CleaningNeedsMaterialHandler,
        // CleaningCancelledHandler) may depend only on the message type,
        // MessageContext, IReservationsMessageExecutionScope and
        // CancellationToken — never directly on persistence/transaction/
        // scope-factory types Wolverine's own codegen would otherwise try to
        // inline.
        var adapterTypes = Types.InAssembly(typeof(ReservationsMessageExecutionScope).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.Reservations.Infrastructure.Messaging")
            .And()
            .DoNotHaveName(nameof(ReservationsMessageExecutionScope))
            .GetTypes();

        adapterTypes.Should().NotBeEmpty("the ten Cleaning lifecycle Wolverine adapter classes are expected to exist in this namespace");

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext",
            "IHostPro.Contexts.Reservations.Application.IReservationsTransactionExecutor",
            "IHostPro.Contexts.Reservations.Infrastructure.Projections.CleaningScheduleProjectionSynchronizer",
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
                $"{adapterType.FullName} must depend only on IReservationsMessageExecutionScope for anything Reservations-persistence-related (ADR-016)");
        }
    }
}
