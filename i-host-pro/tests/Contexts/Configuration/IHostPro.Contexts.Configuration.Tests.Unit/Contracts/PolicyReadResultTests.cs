using FluentAssertions;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Contracts;

public class PolicyReadResultTests
{
    [Fact]
    public void Resolved_carries_the_value_scope_and_version()
    {
        var value = new EarlyCheckInPolicy(true, null, false, false, false);

        var result = PolicyReadResult<EarlyCheckInPolicy>.Resolved(value, PolicyResolvedScope.Tenant, 3);

        result.Status.Should().Be(PolicyReadStatus.Resolved);
        result.Value.Should().Be(value);
        result.ResolvedScope.Should().Be(PolicyResolvedScope.Tenant);
        result.Version.Should().Be(3);
    }

    [Fact]
    public void Resolved_at_Global_scope_allows_a_null_version()
    {
        var value = new EarlyCheckInPolicy(true, null, false, false, false);

        var result = PolicyReadResult<EarlyCheckInPolicy>.Resolved(value, PolicyResolvedScope.Global, null);

        result.ResolvedScope.Should().Be(PolicyResolvedScope.Global);
        result.Version.Should().BeNull();
    }

    [Fact]
    public void NotConfigured_carries_no_value_scope_or_version()
    {
        var result = PolicyReadResult<EarlyCheckInPolicy>.NotConfigured();

        result.Status.Should().Be(PolicyReadStatus.NotConfigured);
        result.Value.Should().BeNull();
        result.ResolvedScope.Should().BeNull();
        result.Version.Should().BeNull();
    }
}
