using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using Xunit;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Domain;

public class PropertyTests
{
    private static readonly Address SomeAddress = Address.Create(
        "01310100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "SP");

    [Fact]
    public void Create_with_own_address_and_no_condominium_succeeds_and_starts_as_Draft()
    {
        var property = Property.Create(
            Guid.NewGuid(), Guid.NewGuid(), PropertyCode.Create("A1"), "Studio A1", 2,
            condominiumId: null, address: SomeAddress, DateTimeOffset.UtcNow);

        property.Status.Should().Be(PropertyStatus.Draft);
        property.Address.Should().Be(SomeAddress);
    }

    [Fact]
    public void Create_with_condominium_and_no_own_address_succeeds()
    {
        var property = Property.Create(
            Guid.NewGuid(), Guid.NewGuid(), PropertyCode.Create("A1"), "Studio A1", 2,
            condominiumId: Guid.NewGuid(), address: null, DateTimeOffset.UtcNow);

        property.Address.Should().BeNull();
    }

    [Fact]
    public void Create_with_neither_condominium_nor_own_address_throws()
    {
        var act = () => Property.Create(
            Guid.NewGuid(), Guid.NewGuid(), PropertyCode.Create("A1"), "Studio A1", 2,
            condominiumId: null, address: null, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_both_condominium_and_own_address_succeeds()
    {
        var property = Property.Create(
            Guid.NewGuid(), Guid.NewGuid(), PropertyCode.Create("A1"), "Studio A1", 2,
            condominiumId: Guid.NewGuid(), address: SomeAddress, DateTimeOffset.UtcNow);

        property.Address.Should().Be(SomeAddress);
        property.CondominiumId.Should().NotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_non_positive_capacity(int capacity)
    {
        var act = () => Property.Create(
            Guid.NewGuid(), Guid.NewGuid(), PropertyCode.Create("A1"), "Studio A1", capacity,
            condominiumId: Guid.NewGuid(), address: null, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_name(string name)
    {
        var act = () => Property.Create(
            Guid.NewGuid(), Guid.NewGuid(), PropertyCode.Create("A1"), name, 2,
            condominiumId: Guid.NewGuid(), address: null, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NormalizedCode_mirrors_the_code_Value_Objects_normalized_form()
    {
        var property = Property.Create(
            Guid.NewGuid(), Guid.NewGuid(), PropertyCode.Create("apt-101"), "Studio A1", 2,
            condominiumId: Guid.NewGuid(), address: null, DateTimeOffset.UtcNow);

        property.NormalizedCode.Should().Be("APT-101");
    }
}
