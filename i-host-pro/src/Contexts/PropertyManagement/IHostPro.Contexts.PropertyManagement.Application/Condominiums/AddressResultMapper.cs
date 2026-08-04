using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Unwraps the Domain <see cref="Address"/> Value Object into the plain
/// <see cref="AddressResult"/> shape — public because both this assembly's
/// own Command handlers and Infrastructure's <c>CondominiumReader</c> use it,
/// the same way <c>UserResult</c>'s own unwrapping is a shared concern.
/// </summary>
public static class AddressResultMapper
{
    public static AddressResult ToResult(Address address) => new(
        address.ZipCode, address.Street, address.Number, address.Complement,
        address.Neighborhood, address.City, address.State, address.Country);
}
