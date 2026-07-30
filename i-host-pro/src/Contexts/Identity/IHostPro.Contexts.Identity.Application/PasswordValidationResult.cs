namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Outcome of <see cref="IUserProvisioningService.ValidatePasswordAsync"/>.
/// <see cref="ErrorCodes"/> are the framework's own <c>IdentityError.Code</c>
/// values (e.g. <c>"PasswordTooShort"</c>) — never re-derived or
/// re-implemented (Incremento 3, Checkpoint 5: "reutilizar integralmente a
/// política de senha existente"). Deliberately not an <c>Identity.*</c>
/// <see cref="IHostPro.BuildingBlocks.Domain.Error"/> code: this is an input
/// validation failure (maps to 400), not a business-state rejection.
/// </summary>
public sealed record PasswordValidationResult(bool Succeeded, IReadOnlyCollection<string> ErrorCodes)
{
    public static readonly PasswordValidationResult Success = new(true, []);

    public static PasswordValidationResult Failure(IReadOnlyCollection<string> errorCodes) => new(false, errorCodes);
}
