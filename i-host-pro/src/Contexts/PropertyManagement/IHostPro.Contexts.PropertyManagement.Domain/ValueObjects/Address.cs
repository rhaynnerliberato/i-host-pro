using System.Text.RegularExpressions;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

/// <summary>
/// Brazilian postal address, without geolocation (Checkpoint 0/1 plan —
/// latitude/longitude/geocoding are reserved for a future "Localização"
/// capability, out of scope for this increment). <see cref="Condominium"/>
/// always has one; <see cref="Property"/> has one only when it has no
/// <see cref="Condominium"/> (Checkpoint 0 plan, item 5).
/// </summary>
public sealed partial class Address : ValueObject
{
    public string ZipCode { get; }
    public string Street { get; }
    public string Number { get; }
    public string? Complement { get; }
    public string Neighborhood { get; }
    public string City { get; }
    public string State { get; }
    public string Country { get; }

    private Address(
        string zipCode, string street, string number, string? complement,
        string neighborhood, string city, string state, string country)
    {
        ZipCode = zipCode;
        Street = street;
        Number = number;
        Complement = complement;
        Neighborhood = neighborhood;
        City = city;
        State = state;
        Country = country;
    }

    public static Address Create(
        string zipCode, string street, string number, string? complement,
        string neighborhood, string city, string state, string country = "BR")
    {
        var normalizedZipCode = NormalizeZipCode(zipCode);
        var trimmedStreet = RequireNotEmpty(street, nameof(street), 150);
        var trimmedNumber = RequireNotEmpty(number, nameof(number), 20);
        var trimmedComplement = TrimOptional(complement, nameof(complement), 100);
        var trimmedNeighborhood = RequireNotEmpty(neighborhood, nameof(neighborhood), 100);
        var trimmedCity = RequireNotEmpty(city, nameof(city), 100);
        var trimmedState = RequireNotEmpty(state, nameof(state), 2).ToUpperInvariant();
        var trimmedCountry = RequireNotEmpty(country, nameof(country), 2).ToUpperInvariant();

        return new Address(
            normalizedZipCode, trimmedStreet, trimmedNumber, trimmedComplement,
            trimmedNeighborhood, trimmedCity, trimmedState, trimmedCountry);
    }

    /// <summary>
    /// Accepts either 8 raw digits or the <c>NNNNN-NNN</c> display format;
    /// always persists 8 digits (Checkpoint 0 plan, item 5). No external CEP
    /// lookup is performed.
    /// </summary>
    private static string NormalizeZipCode(string zipCode)
    {
        if (string.IsNullOrWhiteSpace(zipCode))
            throw new ArgumentException("Zip code cannot be empty.", nameof(zipCode));

        var digitsOnly = NonDigitRegex().Replace(zipCode.Trim(), string.Empty);

        if (digitsOnly.Length != 8)
            throw new ArgumentException($"'{zipCode}' is not a valid zip code.", nameof(zipCode));

        return digitsOnly;
    }

    private static string RequireNotEmpty(string value, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} cannot be empty.", paramName);

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{paramName} cannot exceed {maxLength} characters.", paramName);

        return trimmed;
    }

    private static string? TrimOptional(string? value, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{paramName} cannot exceed {maxLength} characters.", paramName);

        return trimmed;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return ZipCode;
        yield return Street;
        yield return Number;
        yield return Complement;
        yield return Neighborhood;
        yield return City;
        yield return State;
        yield return Country;
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigitRegex();
}
