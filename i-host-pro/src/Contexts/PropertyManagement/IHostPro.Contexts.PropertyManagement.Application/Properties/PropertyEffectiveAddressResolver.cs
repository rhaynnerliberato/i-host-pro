using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Resolves a property's own/effective address/source triple for a
/// <see cref="PropertyResult"/> response — shared by the three lifecycle
/// handlers (Checkpoint 4 plan), which all need the same resolution purely
/// to build their response, never as a precondition of Deactivate/Archive
/// themselves (only <c>ActivatePropertyCommandHandler</c> also uses the
/// failure branch as a genuine activation precondition — Checkpoint 4 plan,
/// item 5). Not shared with Create/Update's own handlers (Checkpoint 3),
/// which have unresolved candidate values, not an already-persisted
/// <see cref="Property"/>, at the point they need this — deliberately not
/// refactored to avoid touching already-approved Checkpoint 3 code.
/// </summary>
internal static class PropertyEffectiveAddressResolver
{
    private static readonly Error CondominiumNotFoundError = new(
        PropertyManagementErrorCodes.CondominiumNotFound, PropertyManagementErrorCodes.CondominiumNotFound);
    private static readonly Error PropertyAddressRequiredError = new(
        PropertyManagementErrorCodes.PropertyAddressRequired, PropertyManagementErrorCodes.PropertyAddressRequired);

    public static async Task<Result<(AddressResult? Own, AddressResult Effective, string Source)>> ResolveAsync(
        Property property, ICondominiumReader condominiumReader, CancellationToken cancellationToken)
    {
        if (property.Address is not null)
        {
            var own = AddressResultMapper.ToResult(property.Address);
            return Result.Success<(AddressResult?, AddressResult, string)>((own, own, "property"));
        }

        if (property.CondominiumId is not Guid condominiumId)
            return Result.Failure<(AddressResult?, AddressResult, string)>(PropertyAddressRequiredError);

        var condominiumAddress = await condominiumReader.GetAddressByIdAsync(condominiumId, cancellationToken);
        if (condominiumAddress is null)
            return Result.Failure<(AddressResult?, AddressResult, string)>(CondominiumNotFoundError);

        return Result.Success<(AddressResult?, AddressResult, string)>((null, condominiumAddress, "condominium"));
    }
}
