using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Domain;
using Xunit;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Domain;

public class PropertyAuditEntryTests
{
    [Fact]
    public void Create_sets_all_fields()
    {
        var tenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var changedFields = new[] { "name", "capacity" };

        var entry = PropertyAuditEntry.Create(
            Guid.NewGuid(), tenantId, actorUserId, "Property", aggregateId, "property_updated", changedFields, now);

        entry.TenantId.Should().Be(tenantId);
        entry.ActorUserId.Should().Be(actorUserId);
        entry.EntityType.Should().Be("Property");
        entry.AggregateId.Should().Be(aggregateId);
        entry.ActionCode.Should().Be("property_updated");
        entry.ChangedFields.Should().BeEquivalentTo(changedFields);
        entry.OccurredAt.Should().Be(now);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_entity_type(string entityType)
    {
        var act = () => PropertyAuditEntry.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), entityType, Guid.NewGuid(), "property_updated",
            Array.Empty<string>(), DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_action_code(string actionCode)
    {
        var act = () => PropertyAuditEntry.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Property", Guid.NewGuid(), actionCode,
            Array.Empty<string>(), DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
