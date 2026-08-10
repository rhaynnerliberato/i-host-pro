using FluentAssertions;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Domain;

public class PolicyVersionTests
{
    [Fact]
    public void First_is_1()
    {
        PolicyVersion.First().Value.Should().Be(1);
    }

    [Fact]
    public void Next_increments_by_1()
    {
        PolicyVersion.First().Next().Value.Should().Be(2);
        PolicyVersion.Create(5).Next().Value.Should().Be(6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_values_below_1(int value)
    {
        var act = () => PolicyVersion.Create(value);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Two_versions_with_the_same_value_are_equal()
    {
        PolicyVersion.Create(3).Should().Be(PolicyVersion.Create(3));
    }
}
