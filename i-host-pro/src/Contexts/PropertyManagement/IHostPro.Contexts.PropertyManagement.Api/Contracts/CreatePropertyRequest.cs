namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>
/// Every field is nullable at the wire level — presence/format is validated
/// by the Application layer (<c>CreatePropertyCommandValidator</c>/
/// <c>PropertyCode.Create</c>/<c>Address.Create</c>), never here (mirrors
/// <c>CreateCondominiumRequest</c>'s convention). No <c>status</c> field: the
/// client can never set it — every property is born in Draft (Checkpoint 3
/// plan, item 3).
/// </summary>
public sealed record CreatePropertyRequest(
    string? Code,
    string? Name,
    int? Capacity,
    Guid? CondominiumId,
    AddressRequest? Address);
