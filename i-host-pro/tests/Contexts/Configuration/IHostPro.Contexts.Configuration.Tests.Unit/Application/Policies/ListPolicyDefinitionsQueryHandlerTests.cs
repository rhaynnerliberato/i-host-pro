using FluentAssertions;
using IHostPro.Contexts.Configuration.Application.Policies;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

public class ListPolicyDefinitionsQueryHandlerTests
{
    [Fact]
    public async Task Returns_every_definition_from_the_reader()
    {
        var definitions = new[]
        {
            new PolicyDefinitionResult("EARLY_CHECKIN", "Early Check-in", "d", "CHECK_IN_OUT", "Object", 1, true),
            new PolicyDefinitionResult("LATE_CHECKOUT", "Late Checkout", "d", "CHECK_IN_OUT", "Object", 1, true),
        };
        var handler = new ListPolicyDefinitionsQueryHandler(FakePolicyDefinitionReader.WithDefinitions(definitions));

        var result = await handler.Handle(new ListPolicyDefinitionsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(definitions);
    }
}
