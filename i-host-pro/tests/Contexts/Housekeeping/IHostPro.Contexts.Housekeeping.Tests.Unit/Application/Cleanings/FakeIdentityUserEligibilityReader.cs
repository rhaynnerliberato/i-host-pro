using IHostPro.Contexts.Identity.Contracts;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakeIdentityUserEligibilityReader : IIdentityUserEligibilityReader
{
    private readonly IdentityUserEligibility? _eligibility;

    private FakeIdentityUserEligibilityReader(IdentityUserEligibility? eligibility) => _eligibility = eligibility;

    public static FakeIdentityUserEligibilityReader With(IdentityUserEligibility? eligibility) => new(eligibility);

    public string? LastRequiredRoleCode { get; private set; }

    public Task<IdentityUserEligibility?> GetAsync(
        Guid tenantId, Guid userId, string requiredRoleCode, CancellationToken cancellationToken)
    {
        LastRequiredRoleCode = requiredRoleCode;
        return Task.FromResult(_eligibility);
    }
}
