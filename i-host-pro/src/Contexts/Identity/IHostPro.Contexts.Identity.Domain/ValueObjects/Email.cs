using System.Text.RegularExpressions;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Domain.ValueObjects;

/// <summary>
/// Email address, used both as the user's contact address and as the
/// ASP.NET Core Identity "username" (Incremento 1 plan, Section 2) — there is
/// no separate username concept in this Domain.
/// </summary>
public sealed partial class Email : ValueObject
{
    public string Value { get; }

    public string NormalizedValue { get; }

    private Email(string value, string normalizedValue)
    {
        Value = value;
        NormalizedValue = normalizedValue;
    }

    public static Email Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Email cannot be empty.", nameof(value));

        var trimmed = value.Trim();
        if (!FormatRegex().IsMatch(trimmed))
            throw new ArgumentException($"'{value}' is not a valid email format.", nameof(value));

        return new Email(trimmed, EmailNormalizer.Normalize(trimmed));
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return NormalizedValue;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex FormatRegex();
}
