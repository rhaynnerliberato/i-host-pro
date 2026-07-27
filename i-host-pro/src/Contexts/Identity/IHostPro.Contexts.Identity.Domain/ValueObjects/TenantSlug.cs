using System.Text.RegularExpressions;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Domain.ValueObjects;

/// <summary>
/// Unique, normalized, human-friendly tenant identifier, presented by the
/// client during login (before any tenant claim exists) to resolve which
/// tenant to authenticate against (Incremento 1 plan, Section 8). Immutable
/// after provisioning in this phase — no rename operation exists yet.
/// Format is deliberately DNS-label-compatible, in case a future phase
/// resolves tenants by (sub)domain instead — that resolution mode is not
/// implemented now.
/// </summary>
public sealed partial class TenantSlug : ValueObject
{
    public string Value { get; }

    private TenantSlug(string value) => Value = value;

    public static TenantSlug Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Tenant slug cannot be empty.", nameof(value));

        var normalized = value.Trim().ToLowerInvariant();
        if (!FormatRegex().IsMatch(normalized))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid tenant slug (expected 3-63 lowercase alphanumeric/hyphen characters).",
                nameof(value));
        }

        return new TenantSlug(normalized);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9-]{3,63}$")]
    private static partial Regex FormatRegex();
}
