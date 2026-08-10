using FluentAssertions;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Domain;

public class PolicyDefinitionTests
{
    [Fact]
    public void Create_with_valid_data_succeeds()
    {
        var definition = new PolicyDefinition(
            "EARLY_CHECKIN", "Early Check-in", "Description", "CHECK_IN_OUT",
            PolicyValueType.Object, schemaVersion: 1, isActive: true);

        definition.Id.Should().Be("EARLY_CHECKIN");
        definition.Name.Should().Be("Early Check-in");
        definition.Category.Should().Be("CHECK_IN_OUT");
        definition.ValueType.Should().Be(PolicyValueType.Object);
        definition.SchemaVersion.Should().Be(1);
        definition.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Name", "Description", "Category")]
    [InlineData("CODE", "", "Description", "Category")]
    [InlineData("CODE", "Name", "", "Category")]
    [InlineData("CODE", "Name", "Description", "")]
    public void Create_rejects_empty_required_strings(string code, string name, string description, string category)
    {
        var act = () => new PolicyDefinition(code, name, description, category, PolicyValueType.Object, 1, true);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_a_schema_version_below_1()
    {
        var act = () => new PolicyDefinition("CODE", "Name", "Description", "Category", PolicyValueType.Object, 0, true);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
