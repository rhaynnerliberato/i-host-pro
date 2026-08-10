using FluentAssertions;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Domain;

public class GlobalPolicyValueTests
{
    [Fact]
    public void Create_with_valid_data_succeeds()
    {
        var value = GlobalPolicyValue.Create(
            Guid.NewGuid(), "EARLY_CHECKIN", """{"allowed":true}""", DateTimeOffset.UtcNow);

        value.PolicyCode.Should().Be("EARLY_CHECKIN");
        value.Value.Should().Be("""{"allowed":true}""");
    }

    [Fact]
    public void Create_rejects_an_empty_policy_code()
    {
        var act = () => GlobalPolicyValue.Create(Guid.NewGuid(), " ", """{"allowed":true}""", DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_an_empty_value()
    {
        var act = () => GlobalPolicyValue.Create(Guid.NewGuid(), "EARLY_CHECKIN", " ", DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
