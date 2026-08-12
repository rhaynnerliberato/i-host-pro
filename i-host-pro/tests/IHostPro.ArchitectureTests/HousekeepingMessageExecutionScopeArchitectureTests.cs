using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces ADR-015 (Fase 6, Checkpoint 6) — Isolamento do Processamento de
/// Mensagens Housekeeping da Integração EF Core do Wolverine. The user
/// approved <c>opts.CodeGeneration.AlwaysUseServiceLocationFor&lt;
/// IHousekeepingMessageExecutionScope&gt;()</c> exclusively for the single
/// boundary that holds <see cref="IServiceScopeFactory"/> — every other
/// class in the Bounded Context, including the six thin Wolverine adapters
/// themselves, must keep depending only on the ordinary constructor-injected
/// graph. These tests make that boundary a build-breaking guarantee instead
/// of a comment.
/// </summary>
public class HousekeepingMessageExecutionScopeArchitectureTests
{
    private static readonly Type[] HousekeepingAssemblyAnchors =
    [
        typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning),
        typeof(IHostPro.Contexts.Housekeeping.Contracts.AssemblyReference),
        typeof(IHousekeepingMessageExecutionScope),
        typeof(HousekeepingMessageExecutionScope),
        typeof(IHostPro.Contexts.Housekeeping.Api.AssemblyReference),
    ];

    [Fact]
    public void Only_HousekeepingMessageExecutionScope_May_Depend_On_IServiceScopeFactory()
    {
        // IServiceScopeFactory is a Singleton — resolving it anywhere else in
        // Housekeeping would either bypass the tenant-execution boundary
        // ADR-015 establishes, or (per Wolverine's own codegen) risk
        // reproducing the exact InvalidServiceLocationException this
        // boundary already exists to correct. Not "should be rare" — must be
        // exactly one type, in exactly one assembly.
        var typesDependingOnScopeFactory = HousekeepingAssemblyAnchors
            .Select(anchor => anchor.Assembly)
            .Distinct()
            .SelectMany(assembly => Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn("Microsoft.Extensions.DependencyInjection.IServiceScopeFactory")
                .GetTypes())
            .Distinct()
            .ToList();

        typesDependingOnScopeFactory.Should().ContainSingle()
            .Which.Should().Be(typeof(HousekeepingMessageExecutionScope),
                "IHousekeepingMessageExecutionScope's own implementation is the single, deliberately-authorized " +
                "holder of IServiceScopeFactory in Housekeeping (ADR-015) — any other match means a new class " +
                "started resolving its own child scope outside the approved boundary.");
    }

    [Fact]
    public void Wolverine_Adapters_Never_Depend_On_HousekeepingDbContext_Or_TransactionExecutor_Or_ServiceScopeFactory()
    {
        // The six thin Wolverine entrypoints (ReservationCreatedHandler,
        // ReservationCancelledHandler, PropertyCreatedHandler,
        // PropertyActivatedHandler, PropertyDeactivatedHandler,
        // PropertyArchivedHandler) may depend only on the message type,
        // MessageContext, IHousekeepingMessageExecutionScope and
        // CancellationToken — never directly on persistence/transaction/
        // scope-factory types Wolverine's own codegen would otherwise try to
        // inline. This is the static counterpart of the real-Worker
        // discovery proof in HousekeepingWolverineDiscoveryTests.
        var adapterTypes = Types.InAssembly(typeof(HousekeepingMessageExecutionScope).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.Housekeeping.Infrastructure.Messaging")
            .And()
            .DoNotHaveName(nameof(HousekeepingMessageExecutionScope))
            .GetTypes();

        adapterTypes.Should().NotBeEmpty("the six Wolverine adapter classes are expected to exist in this namespace");

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext",
            "IHostPro.Contexts.Housekeeping.Application.IHousekeepingTransactionExecutor",
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
                $"{adapterType.FullName} must depend only on IHousekeepingMessageExecutionScope for anything Housekeeping-persistence-related (ADR-015)");
        }
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
