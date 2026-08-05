using IHostPro.Contexts.PropertyManagement.Contracts;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

internal sealed class FakePropertyReservationEligibilityReader : IPropertyReservationEligibilityReader
{
    private readonly PropertyReservationEligibility? _eligibility;

    private FakePropertyReservationEligibilityReader(PropertyReservationEligibility? eligibility) => _eligibility = eligibility;

    public static FakePropertyReservationEligibilityReader With(PropertyReservationEligibility? eligibility) => new(eligibility);

    public List<Guid> RequestedPropertyIds { get; } = [];

    public Task<PropertyReservationEligibility?> GetAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken)
    {
        RequestedPropertyIds.Add(propertyId);
        return Task.FromResult(_eligibility);
    }
}
