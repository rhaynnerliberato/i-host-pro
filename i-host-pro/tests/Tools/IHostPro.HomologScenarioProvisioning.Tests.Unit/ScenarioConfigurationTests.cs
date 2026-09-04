using FluentAssertions;
using IHostPro.HomologScenarioProvisioning;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IHostPro.HomologScenarioProvisioning.Tests.Unit;

public class ScenarioConfigurationTests
{
    [Fact]
    public void Missing_key_throws_a_clear_error()
    {
        var configuration = new ConfigurationBuilder().Build();

        var act = () => ScenarioConfiguration.RequireConfig(configuration, "HomologScenarioProvisioning:TenantId");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*HomologScenarioProvisioning:TenantId*");
    }

    [Fact]
    public void A_real_value_is_returned_unchanged()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HomologScenarioProvisioning:TenantId"] = "07edf006-7901-46cc-ac6c-1b18a7d63bb7" })
            .Build();

        ScenarioConfiguration.RequireConfig(configuration, "HomologScenarioProvisioning:TenantId")
            .Should().Be("07edf006-7901-46cc-ac6c-1b18a7d63bb7");
    }
}
