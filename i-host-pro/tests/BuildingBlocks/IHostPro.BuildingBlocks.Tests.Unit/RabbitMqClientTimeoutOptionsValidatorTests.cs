using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;

namespace IHostPro.BuildingBlocks.Tests.Unit;

public class RabbitMqClientTimeoutOptionsValidatorTests
{
    [Fact]
    public void ValidateAndThrow_succeeds_for_the_documented_defaults()
    {
        var act = () => RabbitMqClientTimeoutOptionsValidator.ValidateAndThrow(new RabbitMqClientTimeoutOptions());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public void ValidateAndThrow_throws_when_ConnectTimeout_is_below_the_minimum(int milliseconds)
    {
        var options = new RabbitMqClientTimeoutOptions { ConnectTimeout = TimeSpan.FromMilliseconds(milliseconds) };

        var act = () => RabbitMqClientTimeoutOptionsValidator.ValidateAndThrow(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectTimeout*");
    }

    [Fact]
    public void ValidateAndThrow_throws_when_ConnectTimeout_exceeds_the_maximum()
    {
        var options = new RabbitMqClientTimeoutOptions { ConnectTimeout = TimeSpan.FromSeconds(31) };

        var act = () => RabbitMqClientTimeoutOptionsValidator.ValidateAndThrow(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectTimeout*");
    }

    [Fact]
    public void ValidateAndThrow_throws_when_ContinuationTimeout_is_below_the_minimum()
    {
        var options = new RabbitMqClientTimeoutOptions { ContinuationTimeout = TimeSpan.FromMilliseconds(100) };

        var act = () => RabbitMqClientTimeoutOptionsValidator.ValidateAndThrow(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ContinuationTimeout*");
    }

    [Fact]
    public void ValidateAndThrow_throws_when_ContinuationTimeout_exceeds_the_maximum()
    {
        var options = new RabbitMqClientTimeoutOptions { ContinuationTimeout = TimeSpan.FromSeconds(31) };

        var act = () => RabbitMqClientTimeoutOptionsValidator.ValidateAndThrow(options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ContinuationTimeout*");
    }
}
