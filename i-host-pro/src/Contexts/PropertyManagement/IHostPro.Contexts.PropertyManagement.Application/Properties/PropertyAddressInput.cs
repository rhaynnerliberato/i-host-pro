namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// The raw address fields supplied by a caller for
/// <c>CreatePropertyCommand</c>/<c>UpdatePropertyCommand</c> — not yet
/// validated/normalized into a Domain <c>Address</c> Value Object; the
/// handler does that via <c>Address.Create</c> (Checkpoint 3 plan, item 6).
/// Deliberately a separate type from Condominiums' own
/// <c>CondominiumAddressInput</c> (Checkpoint 2) — same shape, but keeping
/// them independent avoids coupling one Command family's contract to
/// another's, and Checkpoint 2's already-approved code is never touched.
/// </summary>
public sealed record PropertyAddressInput(
    string ZipCode,
    string Street,
    string Number,
    string? Complement,
    string Neighborhood,
    string City,
    string State,
    string Country);
