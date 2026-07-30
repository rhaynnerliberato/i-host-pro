namespace IHostPro.Contexts.Identity.Api.Contracts;

public sealed record CreateUserRequest(string? FullName, string? Email, string? InitialPassword, string? RoleCode);
