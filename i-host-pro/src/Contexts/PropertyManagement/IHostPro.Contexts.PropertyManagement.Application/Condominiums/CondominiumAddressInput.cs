namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// The raw address fields supplied by a caller for
/// <c>CreateCondominiumCommand</c>/<c>UpdateCondominiumCommand</c> — not yet
/// validated/normalized into a Domain <c>Address</c> Value Object; the
/// handler does that via <c>Address.Create</c> (Checkpoint 2 plan, item 6).
/// On update, a complete replacement is always required when supplied — no
/// partial/field-level patch of an existing address (Checkpoint 2 plan, item
/// 4/6).
/// </summary>
public sealed record CondominiumAddressInput(
    string ZipCode,
    string Street,
    string Number,
    string? Complement,
    string Neighborhood,
    string City,
    string State,
    string Country);
