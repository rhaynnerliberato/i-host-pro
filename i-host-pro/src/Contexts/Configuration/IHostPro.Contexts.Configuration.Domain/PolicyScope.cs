using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// Identifies a single level in the policy resolution hierarchy: a
/// <see cref="PolicyScopeType"/> plus, only for <see cref="PolicyScopeType.Property"/>,
/// the opaque Property identifier it applies to — carries no physical
/// reference (no foreign key) to Property Management's own aggregate, same
/// opaque-Guid convention already used by <c>Reservation.PropertyId</c>.
/// </summary>
public sealed class PolicyScope : ValueObject
{
    public PolicyScopeType Type { get; }
    public Guid? ReferenceId { get; }

    private PolicyScope(PolicyScopeType type, Guid? referenceId)
    {
        Type = type;
        ReferenceId = referenceId;
    }

    public static PolicyScope Create(PolicyScopeType type, Guid? referenceId)
    {
        switch (type)
        {
            case PolicyScopeType.Property:
                if (referenceId is null || referenceId == Guid.Empty)
                    throw new ArgumentException("Property scope requires a non-empty reference id.", nameof(referenceId));
                break;
            case PolicyScopeType.Tenant:
            case PolicyScopeType.Global:
                if (referenceId is not null)
                    throw new ArgumentException($"{type} scope must not carry a reference id.", nameof(referenceId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown policy scope type.");
        }

        return new PolicyScope(type, referenceId);
    }

    public static PolicyScope Tenant() => new(PolicyScopeType.Tenant, null);

    public static PolicyScope Property(Guid propertyId) => Create(PolicyScopeType.Property, propertyId);

    public static PolicyScope Global() => new(PolicyScopeType.Global, null);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Type;
        yield return ReferenceId;
    }
}
