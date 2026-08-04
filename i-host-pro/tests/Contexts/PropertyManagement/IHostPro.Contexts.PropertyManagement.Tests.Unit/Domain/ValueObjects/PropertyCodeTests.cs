using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using Xunit;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Domain.ValueObjects;

public class PropertyCodeTests
{
    [Theory]
    [InlineData("A1")]
    [InlineData("apt-101")]
    [InlineData("apt_101")]
    [InlineData("apt.101")]
    [InlineData("1")]
    public void Create_accepts_valid_codes(string value)
    {
        var code = PropertyCode.Create(value);

        code.Value.Should().Be(value);
    }

    [Fact]
    public void Create_preserves_display_casing_after_trim()
    {
        var code = PropertyCode.Create("  Apt-101  ");

        code.Value.Should().Be("Apt-101");
    }

    [Fact]
    public void Create_normalizes_to_upper_invariant()
    {
        var code = PropertyCode.Create("apt-101");

        code.NormalizedValue.Should().Be("APT-101");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_value(string value)
    {
        var act = () => PropertyCode.Create(value);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("-apt101")] // must start with alphanumeric
    [InlineData("_apt101")]
    [InlineData(".apt101")]
    [InlineData("apt 101")] // no spaces
    [InlineData("apt#101")] // no other punctuation
    public void Create_rejects_invalid_format(string value)
    {
        var act = () => PropertyCode.Create(value);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_value_longer_than_fifty_characters()
    {
        var tooLong = new string('a', 51);

        var act = () => PropertyCode.Create(tooLong);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_accepts_value_exactly_fifty_characters()
    {
        var maxLength = new string('a', 50);

        var code = PropertyCode.Create(maxLength);

        code.Value.Should().HaveLength(50);
    }

    [Fact]
    public void Two_codes_differing_only_by_case_are_equal()
    {
        var a = PropertyCode.Create("apt-101");
        var b = PropertyCode.Create("APT-101");

        a.Should().Be(b);
    }
}
