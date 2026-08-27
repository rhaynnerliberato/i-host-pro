using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Domain;

public class FrontDeskContactTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_display_name_and_phone_number()
    {
        var contact = FrontDeskContact.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "  Portaria Bloco A  ", "  +5511977776666  ", isActive: true, Now);

        contact.DisplayName.Should().Be("Portaria Bloco A");
        contact.PhoneNumber.Should().Be("+5511977776666");
        contact.IsActive.Should().BeTrue();
        contact.CreatedAtUtc.Should().Be(Now);
        contact.UpdatedAtUtc.Should().Be(Now);
    }

    [Theory]
    [InlineData("", "+5511977776666")]
    [InlineData("   ", "+5511977776666")]
    [InlineData("Portaria Bloco A", "")]
    [InlineData("Portaria Bloco A", "   ")]
    public void Create_rejects_empty_display_name_or_phone_number(string displayName, string phoneNumber)
    {
        var act = () => FrontDeskContact.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), displayName, phoneNumber, true, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateContact_replaces_fields_and_bumps_UpdatedAtUtc()
    {
        var contact = FrontDeskContact.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Portaria Bloco A", "+5511977776666", true, Now);
        var later = Now.AddDays(1);

        contact.UpdateContact("Portaria Bloco B", "+5511988885555", isActive: false, later);

        contact.DisplayName.Should().Be("Portaria Bloco B");
        contact.PhoneNumber.Should().Be("+5511988885555");
        contact.IsActive.Should().BeFalse();
        contact.UpdatedAtUtc.Should().Be(later);
        contact.CreatedAtUtc.Should().Be(Now, "CreatedAtUtc never changes on update");
    }

    [Fact]
    public void UpdateContact_rejects_empty_display_name_or_phone_number()
    {
        var contact = FrontDeskContact.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Portaria Bloco A", "+5511977776666", true, Now);

        var act = () => contact.UpdateContact("", "+5511988885555", true, Now.AddDays(1));

        act.Should().Throw<ArgumentException>();
    }
}
