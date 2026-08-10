using FluentAssertions;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Application.Policies;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

public class GetPolicyHistoryQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Returns_policy_not_found_for_an_unknown_code()
    {
        var handler = new GetPolicyHistoryQueryHandler(
            FakePolicyDefinitionReader.WithCodes(), FakePolicyValueReader.WithHistory());

        var result = await handler.Handle(
            new GetPolicyHistoryQuery(TenantId, "NOT_A_REAL_CODE", "Tenant", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.PolicyNotFound);
    }

    [Fact]
    public async Task Returns_an_empty_list_when_no_version_exists()
    {
        var handler = new GetPolicyHistoryQueryHandler(
            FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueReader.WithHistory());

        var result = await handler.Handle(
            new GetPolicyHistoryQuery(TenantId, "EARLY_CHECKIN", "Tenant", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_every_version_newest_first()
    {
        var v1 = new PolicyValueDetailResult(Guid.NewGuid(), "EARLY_CHECKIN", "Tenant", null, 1, "{}", DateTimeOffset.UtcNow, Guid.NewGuid(), "r1", false);
        var v2 = new PolicyValueDetailResult(Guid.NewGuid(), "EARLY_CHECKIN", "Tenant", null, 2, "{}", DateTimeOffset.UtcNow, Guid.NewGuid(), "r2", true);
        var handler = new GetPolicyHistoryQueryHandler(
            FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueReader.WithHistory(v2, v1));

        var result = await handler.Handle(
            new GetPolicyHistoryQuery(TenantId, "EARLY_CHECKIN", "Tenant", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(v2, v1);
    }
}
