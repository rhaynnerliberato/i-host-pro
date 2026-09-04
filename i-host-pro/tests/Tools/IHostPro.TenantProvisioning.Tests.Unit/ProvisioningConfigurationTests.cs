using FluentAssertions;
using IHostPro.TenantProvisioning;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IHostPro.TenantProvisioning.Tests.Unit;

public class ProvisioningConfigurationTests
{
    [Fact]
    public void Missing_key_throws_a_clear_error()
    {
        var configuration = new ConfigurationBuilder().Build();

        var act = () => ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:TenantSlug");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TenantProvisioning:TenantSlug*");
    }

    [Fact]
    public void Empty_string_value_is_treated_the_same_as_a_missing_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TenantProvisioning:TenantSlug"] = "" })
            .Build();

        var act = () => ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:TenantSlug");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_real_value_is_returned_unchanged()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TenantProvisioning:TenantSlug"] = "ihostpro-homolog" })
            .Build();

        ProvisioningConfiguration.RequireConfig(configuration, "TenantProvisioning:TenantSlug")
            .Should().Be("ihostpro-homolog");
    }
}
