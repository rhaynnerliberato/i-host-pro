using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakePropertyReferenceProjection : IPropertyReferenceProjection
{
    private readonly bool _isKnownActive;

    private FakePropertyReferenceProjection(bool isKnownActive) => _isKnownActive = isKnownActive;

    public static FakePropertyReferenceProjection With(bool isKnownActive) => new(isKnownActive);

    public Task<bool> IsKnownActivePropertyAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken) =>
        Task.FromResult(_isKnownActive);
}
