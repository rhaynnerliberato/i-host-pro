using FluentAssertions;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Domain;

public class PolicyScopeTests
{
    [Fact]
    public void Tenant_scope_carries_no_reference_id()
    {
        var scope = PolicyScope.Tenant();

        scope.Type.Should().Be(PolicyScopeType.Tenant);
        scope.ReferenceId.Should().BeNull();
    }

    [Fact]
    public void Global_scope_carries_no_reference_id()
    {
        var scope = PolicyScope.Global();

        scope.Type.Should().Be(PolicyScopeType.Global);
        scope.ReferenceId.Should().BeNull();
    }

    [Fact]
    public void Property_scope_carries_the_given_reference_id()
    {
        var propertyId = Guid.NewGuid();

        var scope = PolicyScope.Property(propertyId);

        scope.Type.Should().Be(PolicyScopeType.Property);
        scope.ReferenceId.Should().Be(propertyId);
    }

    [Fact]
    public void Property_scope_rejects_an_empty_reference_id()
    {
        var act = () => PolicyScope.Property(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_a_reference_id_for_Tenant_scope()
    {
        var act = () => PolicyScope.Create(PolicyScopeType.Tenant, Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_a_reference_id_for_Global_scope()
    {
        var act = () => PolicyScope.Create(PolicyScopeType.Global, Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_a_missing_reference_id_for_Property_scope()
    {
        var act = () => PolicyScope.Create(PolicyScopeType.Property, null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Two_scopes_with_the_same_type_and_reference_id_are_equal()
    {
        var propertyId = Guid.NewGuid();

        PolicyScope.Property(propertyId).Should().Be(PolicyScope.Property(propertyId));
        PolicyScope.Tenant().Should().Be(PolicyScope.Tenant());
    }
}
