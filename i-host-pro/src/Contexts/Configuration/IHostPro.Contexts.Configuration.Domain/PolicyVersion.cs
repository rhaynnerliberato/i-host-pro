using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// A <see cref="PolicyValue"/>'s monotonically increasing version number.
/// Versions are never reused or decreased — every new version created for a
/// given scope is strictly the previous version's <see cref="Next"/> (Fase 5,
/// Incremento 1 official decisions §4: "Nunca sobrescrever destrutivamente
/// uma versão anterior").
/// </summary>
public sealed class PolicyVersion : ValueObject
{
    public int Value { get; }

    private PolicyVersion(int value) => Value = value;

    public static PolicyVersion First() => new(1);

    public static PolicyVersion Create(int value)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Policy version must be at least 1.");

        return new PolicyVersion(value);
    }

    public PolicyVersion Next() => new(Value + 1);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
