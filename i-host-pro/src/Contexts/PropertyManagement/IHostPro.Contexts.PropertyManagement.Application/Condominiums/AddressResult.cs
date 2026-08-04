namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// A condominium's address as seen by an Administrator (Checkpoint 2 plan,
/// item 4) — plain primitives, not the Domain <c>Address</c> Value Object
/// itself, mirroring <c>UserResult</c>'s convention of unwrapping Domain VOs
/// before they leave Application.
/// </summary>
public sealed record AddressResult(
    string ZipCode,
    string Street,
    string Number,
    string? Complement,
    string Neighborhood,
    string City,
    string State,
    string Country);
