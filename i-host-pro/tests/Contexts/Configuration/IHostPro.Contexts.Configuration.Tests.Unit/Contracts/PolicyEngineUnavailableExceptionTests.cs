using FluentAssertions;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Contracts;

public class PolicyEngineUnavailableExceptionTests
{
    [Fact]
    public void Carries_the_given_message()
    {
        var exception = new PolicyEngineUnavailableException("engine down");

        exception.Message.Should().Be("engine down");
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public void Carries_the_given_message_and_inner_exception()
    {
        var inner = new TimeoutException();

        var exception = new PolicyEngineUnavailableException("engine down", inner);

        exception.Message.Should().Be("engine down");
        exception.InnerException.Should().BeSameAs(inner);
    }
}
