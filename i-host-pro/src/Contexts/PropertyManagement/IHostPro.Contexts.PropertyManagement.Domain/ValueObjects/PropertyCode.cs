using System.Text.RegularExpressions;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

/// <summary>
/// A Property's business identifier, unique per tenant after normalization
/// (Checkpoint 0 plan, item 4). <see cref="Value"/> preserves the casing
/// supplied by the caller (after trimming); <see cref="NormalizedValue"/> is
/// the upper-invariant form the unique constraint
/// (<c>uq_properties_tenant_normalized_code</c>) is built on.
/// </summary>
public sealed partial class PropertyCode : ValueObject
{
    public string Value { get; }

    public string NormalizedValue { get; }

    private PropertyCode(string value, string normalizedValue)
    {
        Value = value;
        NormalizedValue = normalizedValue;
    }

    public static PropertyCode Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Property code cannot be empty.", nameof(value));

        var trimmed = value.Trim();

        if (trimmed.Length is < 1 or > 50)
            throw new ArgumentException("Property code must be between 1 and 50 characters.", nameof(value));

        if (!FormatRegex().IsMatch(trimmed))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid property code (must start with a letter or digit and contain only " +
                "letters, digits, '.', '_' or '-').", nameof(value));
        }

        return new PropertyCode(trimmed, trimmed.ToUpperInvariant());
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return NormalizedValue;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,49}$")]
    private static partial Regex FormatRegex();
}
