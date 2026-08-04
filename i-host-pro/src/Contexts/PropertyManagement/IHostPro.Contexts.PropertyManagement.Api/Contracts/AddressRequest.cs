namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>
/// Every field is nullable at the wire level — presence/format is validated
/// by the Application layer (<c>CreateCondominiumCommandValidator</c>/
/// <c>Address.Create</c>), never here (mirrors <c>CreateUserRequest</c>'s
/// convention). <see cref="Complement"/> explicitly <c>null</c> means "no
/// complement" (Checkpoint 2 plan, item 4: "complement: null remove o
/// complemento").
/// </summary>
public sealed record AddressRequest(
    string? ZipCode,
    string? Street,
    string? Number,
    string? Complement,
    string? Neighborhood,
    string? City,
    string? State,
    string? Country);
