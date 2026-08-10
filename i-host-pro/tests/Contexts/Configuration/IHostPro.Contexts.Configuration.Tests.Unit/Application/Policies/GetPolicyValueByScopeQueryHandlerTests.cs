using FluentAssertions;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Application.Policies;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

public class GetPolicyValueByScopeQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Returns_policy_not_found_for_an_unknown_code()
    {
        var handler = new GetPolicyValueByScopeQueryHandler(
            FakePolicyDefinitionReader.WithCodes(), FakePolicyValueReader.WithCurrent(null));

        var result = await handler.Handle(
            new GetPolicyValueByScopeQuery(TenantId, "NOT_A_REAL_CODE", "Tenant", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.PolicyNotFound);
    }

    [Fact]
    public async Task Returns_forbidden_for_Global_scope()
    {
        var handler = new GetPolicyValueByScopeQueryHandler(
            FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueReader.WithCurrent(null));

        var result = await handler.Handle(
            new GetPolicyValueByScopeQuery(TenantId, "EARLY_CHECKIN", "Global", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Returns_scope_not_supported_for_Property_scope_without_a_propertyId()
    {
        var handler = new GetPolicyValueByScopeQueryHandler(
            FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueReader.WithCurrent(null));

        var result = await handler.Handle(
            new GetPolicyValueByScopeQuery(TenantId, "EARLY_CHECKIN", "Property", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.ScopeNotSupported);
    }

    [Fact]
    public async Task Returns_policy_not_configured_when_no_value_exists_at_the_scope()
    {
        var handler = new GetPolicyValueByScopeQueryHandler(
            FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueReader.WithCurrent(null));

        var result = await handler.Handle(
            new GetPolicyValueByScopeQuery(TenantId, "EARLY_CHECKIN", "Tenant", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.PolicyNotConfigured);
    }

    [Fact]
    public async Task Returns_the_current_value_when_one_exists()
    {
        var current = new PolicyValueDetailResult(
            Guid.NewGuid(), "EARLY_CHECKIN", "Tenant", null, 1, """{"allowed":true}""",
            DateTimeOffset.UtcNow, Guid.NewGuid(), "initial setup", true);
        var handler = new GetPolicyValueByScopeQueryHandler(
            FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueReader.WithCurrent(current));

        var result = await handler.Handle(
            new GetPolicyValueByScopeQuery(TenantId, "EARLY_CHECKIN", "Tenant", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(current);
    }
}
