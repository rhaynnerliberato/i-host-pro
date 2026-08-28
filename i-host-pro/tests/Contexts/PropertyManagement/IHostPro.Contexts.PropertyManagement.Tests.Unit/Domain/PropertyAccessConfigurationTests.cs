using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Domain;

public class PropertyAccessConfigurationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_credential_reference_and_instructions()
    {
        var configuration = PropertyAccessConfiguration.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "  front-door-code  ", "  Wi-Fi: guest / senha: 1234  ", isActive: true, Now);

        configuration.AccessCredentialSecretReference.Should().Be("front-door-code");
        configuration.AccessInstructions.Should().Be("Wi-Fi: guest / senha: 1234");
        configuration.IsActive.Should().BeTrue();
        configuration.CreatedAtUtc.Should().Be(Now);
        configuration.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_normalizes_whitespace_only_fields_to_null()
    {
        var configuration = PropertyAccessConfiguration.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "   ", "   ", isActive: true, Now);

        configuration.AccessCredentialSecretReference.Should().BeNull();
        configuration.AccessInstructions.Should().BeNull();
    }

    [Fact]
    public void Create_allows_both_fields_null()
    {
        var configuration = PropertyAccessConfiguration.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, isActive: false, Now);

        configuration.AccessCredentialSecretReference.Should().BeNull();
        configuration.AccessInstructions.Should().BeNull();
        configuration.IsActive.Should().BeFalse();
    }

    [Fact]
    public void UpdateConfiguration_replaces_fields_and_bumps_UpdatedAtUtc()
    {
        var configuration = PropertyAccessConfiguration.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "front-door-code", "Old instructions", true, Now);
        var later = Now.AddDays(1);

        configuration.UpdateConfiguration("new-code-reference", "New instructions", isActive: false, later);

        configuration.AccessCredentialSecretReference.Should().Be("new-code-reference");
        configuration.AccessInstructions.Should().Be("New instructions");
        configuration.IsActive.Should().BeFalse();
        configuration.UpdatedAtUtc.Should().Be(later);
        configuration.CreatedAtUtc.Should().Be(Now, "CreatedAtUtc never changes on update");
    }

    [Fact]
    public void UpdateConfiguration_can_clear_previously_configured_fields()
    {
        var configuration = PropertyAccessConfiguration.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "front-door-code", "Some instructions", true, Now);

        configuration.UpdateConfiguration(null, null, isActive: true, Now.AddDays(1));

        configuration.AccessCredentialSecretReference.Should().BeNull();
        configuration.AccessInstructions.Should().BeNull();
    }
}
