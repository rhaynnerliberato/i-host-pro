using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards Checkpoint 4-specific structural rules that span multiple layers
/// (Domain/Application/Contracts) rather than a single controller or a
/// source-text pattern — see <see cref="PropertyManagementPropertiesEndpointsArchitectureTests"/>
/// for the Api-layer checks and <see cref="PropertyManagementSourceConventionTests"/>
/// for the source-text ones (Group/Portaria absence, single migration).
/// </summary>
public class PropertyManagementPropertiesLifecycleArchitectureTests
{
    [Fact]
    public void The_three_lifecycle_commands_live_in_Application()
    {
        typeof(ActivatePropertyCommand).Assembly.Should().BeSameAs(typeof(IPropertyReader).Assembly);
        typeof(DeactivatePropertyCommand).Assembly.Should().BeSameAs(typeof(IPropertyReader).Assembly);
        typeof(ArchivePropertyCommand).Assembly.Should().BeSameAs(typeof(IPropertyReader).Assembly);
    }

    [Fact]
    public void The_three_lifecycle_events_live_in_Contracts()
    {
        typeof(PropertyActivated).Assembly.Should().BeSameAs(typeof(PropertyCreated).Assembly);
        typeof(PropertyDeactivated).Assembly.Should().BeSameAs(typeof(PropertyCreated).Assembly);
        typeof(PropertyArchived).Assembly.Should().BeSameAs(typeof(PropertyCreated).Assembly);
    }

    [Fact]
    public void PropertyUpdated_was_never_repurposed_to_represent_a_lifecycle_transition()
    {
        // Checkpoint 4 plan, item 11: "Não alterar PropertyUpdated para
        // representar lifecycle." — its shape must remain exactly what
        // Checkpoint 3 approved: PropertyId + ChangedFields, nothing status-related.
        var propertyNames = typeof(PropertyUpdated)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToArray();

        propertyNames.Should().BeEquivalentTo([nameof(PropertyUpdated.PropertyId), nameof(PropertyUpdated.ChangedFields)]);
    }

    [Fact]
    public void Property_exposes_no_generic_status_setter()
    {
        // Checkpoint 4 plan, item 7: "Não criar método genérico: ChangeStatus;
        // SetStatus; UpdateStatus." — every transition must be its own named
        // method (Activate/Deactivate/Archive), each enforcing its own
        // invariants, never a single setter callers could misuse to jump to
        // an arbitrary status.
        var methodNames = typeof(Property)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToArray();

        string[] forbiddenNames = ["ChangeStatus", "SetStatus", "UpdateStatus"];
        methodNames.Should().NotContain(forbiddenNames);

        methodNames.Should().Contain(nameof(Property.Activate));
        methodNames.Should().Contain(nameof(Property.Deactivate));
        methodNames.Should().Contain(nameof(Property.Archive));
    }
}
