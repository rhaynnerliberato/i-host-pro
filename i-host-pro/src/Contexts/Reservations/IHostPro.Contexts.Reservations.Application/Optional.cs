namespace IHostPro.Contexts.Reservations.Application;

/// <summary>
/// A presence-aware value: distinguishes "not supplied at all" (<see cref="IsSet"/>
/// <c>false</c>) from "supplied, possibly as <c>null</c>" (<see cref="IsSet"/>
/// <c>true</c>, <see cref="Value"/> possibly <c>null</c>) — used by
/// <c>UpdateReservationCommand</c>'s fields. Own copy of
/// <c>PropertyManagement.Application.Optional{T}</c>'s exact shape —
/// deliberately local to this Bounded Context's own Application layer, not
/// <c>BuildingBlocks</c> (Architecture Principles §4: Application layers
/// never reference each other, so this small abstraction is intentionally
/// duplicated per Bounded Context rather than shared — same reasoning
/// Property Management's own copy documents).
/// </summary>
public readonly record struct Optional<T>
{
    public bool IsSet { get; }

    public T? Value { get; }

    private Optional(bool isSet, T? value)
    {
        IsSet = isSet;
        Value = value;
    }

    public static Optional<T> Unset { get; } = new(false, default);

    public static Optional<T> Of(T? value) => new(true, value);
}
