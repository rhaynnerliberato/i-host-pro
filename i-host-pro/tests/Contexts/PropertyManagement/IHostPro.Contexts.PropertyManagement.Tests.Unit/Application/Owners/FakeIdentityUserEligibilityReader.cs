using IHostPro.Contexts.Identity.Contracts;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Owners;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeIdentityUserEligibilityReader : IIdentityUserEligibilityReader
{
    private readonly IdentityUserEligibility? _result;

    private FakeIdentityUserEligibilityReader(IdentityUserEligibility? result) => _result = result;

    public static FakeIdentityUserEligibilityReader WithResult(IdentityUserEligibility? result) => new(result);

    public Guid? LastRequestedTenantId { get; private set; }
    public Guid? LastRequestedUserId { get; private set; }
    public string? LastRequestedRoleCode { get; private set; }
    public int CallCount { get; private set; }

    public Task<IdentityUserEligibility?> GetAsync(
        Guid tenantId, Guid userId, string requiredRoleCode, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestedTenantId = tenantId;
        LastRequestedUserId = userId;
        LastRequestedRoleCode = requiredRoleCode;
        return Task.FromResult(_result);
    }
}
