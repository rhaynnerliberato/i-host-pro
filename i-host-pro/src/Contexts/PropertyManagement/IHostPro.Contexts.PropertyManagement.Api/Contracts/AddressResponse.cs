namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record AddressResponse(
    string ZipCode,
    string Street,
    string Number,
    string? Complement,
    string Neighborhood,
    string City,
    string State,
    string Country);
