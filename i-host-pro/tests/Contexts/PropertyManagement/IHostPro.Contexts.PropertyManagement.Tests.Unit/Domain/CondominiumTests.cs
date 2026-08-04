using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using Xunit;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Domain;

public class CondominiumTests
{
    private static readonly Address SomeAddress = Address.Create(
        "01310100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "SP");

    [Fact]
    public void Create_with_valid_name_and_address_succeeds()
    {
        var condominium = Condominium.Create(Guid.NewGuid(), Guid.NewGuid(), "Edifício Central", SomeAddress, DateTimeOffset.UtcNow);

        condominium.Name.Should().Be("Edifício Central");
        condominium.Address.Should().Be(SomeAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_name(string name)
    {
        var act = () => Condominium.Create(Guid.NewGuid(), Guid.NewGuid(), name, SomeAddress, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_null_address()
    {
        var act = () => Condominium.Create(Guid.NewGuid(), Guid.NewGuid(), "Edifício Central", null!, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_trims_name()
    {
        var condominium = Condominium.Create(Guid.NewGuid(), Guid.NewGuid(), "  Edifício Central  ", SomeAddress, DateTimeOffset.UtcNow);

        condominium.Name.Should().Be("Edifício Central");
    }
}
