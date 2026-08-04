using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using Xunit;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Domain.ValueObjects;

public class AddressTests
{
    [Fact]
    public void Create_normalizes_zip_code_with_dash_to_eight_digits()
    {
        var address = Address.Create("01310-100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "sp");

        address.ZipCode.Should().Be("01310100");
    }

    [Fact]
    public void Create_accepts_zip_code_already_as_eight_digits()
    {
        var address = Address.Create("01310100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "SP");

        address.ZipCode.Should().Be("01310100");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("")]
    public void Create_rejects_zip_code_with_wrong_digit_count(string zipCode)
    {
        var act = () => Address.Create(zipCode, "Street", "1", null, "Neighborhood", "City", "SP");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_trims_and_uppercases_state_and_country()
    {
        var address = Address.Create("01310100", " Av. Paulista ", "1000", null, "Bela Vista", "São Paulo", " sp ", " br ");

        address.State.Should().Be("SP");
        address.Country.Should().Be("BR");
        address.Street.Should().Be("Av. Paulista");
    }

    [Fact]
    public void Create_defaults_country_to_BR()
    {
        var address = Address.Create("01310100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "SP");

        address.Country.Should().Be("BR");
    }

    [Fact]
    public void Create_allows_null_complement()
    {
        var address = Address.Create("01310100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "SP");

        address.Complement.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_required_fields(string blank)
    {
        var act = () => Address.Create("01310100", blank, "1000", null, "Bela Vista", "São Paulo", "SP");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_field_exceeding_max_length()
    {
        var tooLong = new string('a', 151);

        var act = () => Address.Create("01310100", tooLong, "1000", null, "Bela Vista", "São Paulo", "SP");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Two_addresses_with_the_same_values_are_equal()
    {
        var a = Address.Create("01310100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "SP");
        var b = Address.Create("01310-100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "sp");

        a.Should().Be(b);
    }
}
