using FluentAssertions;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Domain;

public class TemplateTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_with_valid_data_starts_active()
    {
        var template = Template.Create(Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "Olá {{GuestName}}", Now);

        template.TenantId.Should().Be(TenantId);
        template.Key.Should().Be("RESERVATION_CONFIRMATION");
        template.Content.Should().Be("Olá {{GuestName}}");
        template.IsActive.Should().BeTrue();
        template.CreatedAtUtc.Should().Be(Now);
        template.UpdatedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_an_empty_key(string key)
    {
        var act = () => Template.Create(Guid.NewGuid(), TenantId, key, "content", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_a_key_longer_than_100_characters()
    {
        var act = () => Template.Create(Guid.NewGuid(), TenantId, new string('K', 101), "content", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_content(string content)
    {
        var act = () => Template.Create(Guid.NewGuid(), TenantId, "KEY", content, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateContent_replaces_content_and_stamps_UpdatedAtUtc()
    {
        var template = Template.Create(Guid.NewGuid(), TenantId, "KEY", "original", Now);
        var later = Now.AddMinutes(5);

        template.UpdateContent("updated", later);

        template.Content.Should().Be("updated");
        template.UpdatedAtUtc.Should().Be(later);
    }

    [Fact]
    public void UpdateContent_rejects_empty_content()
    {
        var template = Template.Create(Guid.NewGuid(), TenantId, "KEY", "original", Now);

        var act = () => template.UpdateContent("", Now.AddMinutes(1));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Deactivate_then_Activate_round_trips_IsActive()
    {
        var template = Template.Create(Guid.NewGuid(), TenantId, "KEY", "content", Now);

        template.Deactivate(Now.AddMinutes(1));
        template.IsActive.Should().BeFalse();

        template.Activate(Now.AddMinutes(2));
        template.IsActive.Should().BeTrue();
    }
}
