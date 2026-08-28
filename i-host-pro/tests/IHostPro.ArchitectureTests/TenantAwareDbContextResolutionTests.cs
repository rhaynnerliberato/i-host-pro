using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Prevents recurrence of the Fase 4 homologation defect: Identity,
/// PropertyManagement and Reservations each registered
/// <c>services.AddScoped&lt;DbContext&gt;(...)</c> aliased to their own concrete
/// DbContext. In the combined Host (IHostPro.Api), the last module registered
/// always won for every consumer of the bare, unparameterized <see cref="DbContext"/>
/// type — including <see cref="TenantAwareUnitOfWork{TDbContext}"/> and
/// several Identity/PropertyManagement readers/writers/executors that
/// injected it directly — regardless of which Bounded Context's command or
/// query was actually executing. Concretely, this made
/// <c>GetOwnProfileQuery</c> (Identity) open its RLS transaction
/// (<c>SET LOCAL app.tenant_id</c>) against <c>ReservationsDbContext</c> while
/// the real query ran against <c>IdentityDbContext</c> on a different
/// connection — PostgreSQL's Row-Level Security then silently returned zero
/// rows, surfacing as an incorrect 404 on <c>GET /api/v1/users/me</c>. See
/// "Fase 4 - Frontend Foundation - Validacao e Homologacao.md" for the full
/// incident record.
///
/// No isolated per-context test host ever exercised this collision — each
/// only registers its own module, so a single registration for the bare
/// <see cref="DbContext"/> type is never ambiguous there. Only composing all
/// three together, exactly as IHostPro.Api's Program.cs does, reproduces it —
/// which is exactly what <see cref="BuildCombinedServices"/> below does.
/// </summary>
public class TenantAwareDbContextResolutionTests
{
    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:PropertyManagement"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:Reservations"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        }).Build();

    /// <summary>
    /// Registers all three Bounded Context modules together, the same way
    /// IHostPro.Api's Program.cs composes them — no connection is ever
    /// opened just by registering (or even inspecting) these descriptors, so
    /// a bogus connection string and no running PostgreSQL are safe here.
    /// </summary>
    private static IServiceCollection BuildCombinedServices()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddIHostProTenantAwarePipeline();

        services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
        services.AddPropertyManagementModule(configuration, isDevelopmentEnvironment: false);
        services.AddReservationsModule(configuration);

        services.AddIdentityCommandDispatch();
        services.AddPropertyManagementCommandDispatch();
        services.AddReservationsCommandDispatch();

        return services;
    }

    [Fact]
    public void No_Infrastructure_type_injects_the_bare_DbContext_base_type()
    {
        Type[] infrastructureAssemblyMarkers =
        [
            typeof(TenantAwareUnitOfWork<>),
            typeof(IdentityDbContext),
            typeof(PropertyManagementDbContext),
            typeof(ReservationsDbContext),
        ];

        var offenders = infrastructureAssemblyMarkers
            .Select(marker => marker.Assembly)
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass)
            .SelectMany(type => type.GetConstructors(), (type, ctor) => (type, ctor))
            .SelectMany(
                pair => pair.ctor.GetParameters(),
                (pair, parameter) => (pair.type, parameter))
            .Where(pair => pair.parameter.ParameterType == typeof(DbContext))
            .Select(pair => $"{pair.type.FullName}({pair.parameter.ParameterType.Name} {pair.parameter.Name})")
            .ToList();

        offenders.Should().BeEmpty(
            "no Infrastructure type may inject the bare DbContext base type — it is ambiguous the moment more " +
            "than one Bounded Context is composed in the same Host; inject the concrete DbContext subclass " +
            "(IdentityDbContext / PropertyManagementDbContext / ReservationsDbContext) instead. Offenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void The_combined_Host_registers_zero_ambiguous_bare_DbContext_services()
    {
        var services = BuildCombinedServices();

        var ambiguousRegistrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(DbContext))
            .Select(descriptor => $"{descriptor.Lifetime} {descriptor.ServiceType}")
            .ToList();

        ambiguousRegistrations.Should().BeEmpty(
            "registering the bare DbContext type is exactly what caused the Fase 4 defect — the last Bounded " +
            "Context module registered always won for every other context's tenant-aware pipeline. Found: " +
            string.Join(", ", ambiguousRegistrations));
    }

    [Fact]
    public void Tenant_aware_pipeline_behaviors_are_wired_to_their_own_contexts_concrete_DbContext()
    {
        var services = BuildCombinedServices();

        var expectedDbContextByContextSegment = new Dictionary<string, Type>
        {
            ["Identity"] = typeof(IdentityDbContext),
            ["PropertyManagement"] = typeof(PropertyManagementDbContext),
            ["Reservations"] = typeof(ReservationsDbContext),
        };

        var tenantAwareBehaviorDefinitions = new[] { typeof(TenantTransactionBehavior<,,>), typeof(TenantBootstrapBehavior<,,>) };

        var mismatches = new List<string>();
        var inspectedCount = 0;

        foreach (var descriptor in services)
        {
            var implementationType = descriptor.ImplementationType;
            if (implementationType is null || !implementationType.IsGenericType)
                continue;

            if (!tenantAwareBehaviorDefinitions.Contains(implementationType.GetGenericTypeDefinition()))
                continue;

            var genericArguments = implementationType.GetGenericArguments();
            var messageType = genericArguments[0];
            var actualDbContextType = genericArguments[2];

            // "IHostPro.Contexts.<Segment>....": the same convention already
            // used by Program.cs's Swagger schemaId disambiguation.
            var contextSegment = messageType.Namespace?
                .Split('.')
                .SkipWhile(segment => segment != "Contexts")
                .Skip(1)
                .FirstOrDefault();

            if (contextSegment is null || !expectedDbContextByContextSegment.TryGetValue(contextSegment, out var expectedDbContextType))
                continue;

            inspectedCount++;
            if (actualDbContextType != expectedDbContextType)
            {
                mismatches.Add(
                    $"{messageType.FullName} is wired to {actualDbContextType.Name}, expected {expectedDbContextType.Name}");
            }
        }

        inspectedCount.Should().BeGreaterThan(0, "expected at least one TenantTransactionBehavior<,,>/TenantBootstrapBehavior<,,> " +
            "registration to inspect for a known Bounded Context — did the registration shape change?");
        mismatches.Should().BeEmpty(string.Join("; ", mismatches));
    }
}
