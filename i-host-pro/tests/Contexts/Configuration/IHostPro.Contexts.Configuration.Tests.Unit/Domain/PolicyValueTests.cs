using FluentAssertions;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Domain;

public class PolicyValueTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public void CreateInitialVersion_starts_at_version_1_and_is_current()
    {
        var value = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "initial setup");

        value.Version.Should().Be(1);
        value.IsCurrent.Should().BeTrue();
        value.ScopeType.Should().Be(PolicyScopeType.Tenant);
        value.ScopeReferenceId.Should().BeNull();
    }

    [Fact]
    public void CreateNextVersion_increments_the_previous_version_and_is_current()
    {
        var value = PolicyValue.CreateNextVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), PolicyVersion.Create(3),
            """{"allowed":false}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "policy change");

        value.Version.Should().Be(4);
        value.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void CreateInitialVersion_for_Property_scope_carries_the_property_id()
    {
        var propertyId = Guid.NewGuid();

        var value = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "LATE_CHECKOUT", PolicyScope.Property(propertyId),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "per-property override");

        value.ScopeType.Should().Be(PolicyScopeType.Property);
        value.ScopeReferenceId.Should().Be(propertyId);
    }

    [Fact]
    public void CreateInitialVersion_rejects_Global_scope()
    {
        var act = () => PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Global(),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "reason");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateInitialVersion_requires_a_mandatory_reason()
    {
        var act = () => PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), reason: "  ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateInitialVersion_rejects_an_empty_value()
    {
        var act = () => PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            value: "  ", DateTimeOffset.UtcNow, Guid.NewGuid(), "reason");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Supersede_flips_IsCurrent_to_false_without_touching_the_value()
    {
        var value = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "initial setup");

        value.Supersede();

        value.IsCurrent.Should().BeFalse();
        value.Value.Should().Be("""{"allowed":true}""");
    }
}
