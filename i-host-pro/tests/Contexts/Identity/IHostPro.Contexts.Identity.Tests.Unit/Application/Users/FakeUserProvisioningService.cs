using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeUserProvisioningService : IUserProvisioningService
{
    private readonly PasswordValidationResult _validationResult;

    private FakeUserProvisioningService(PasswordValidationResult validationResult) => _validationResult = validationResult;

    public static FakeUserProvisioningService ThatAccepts() => new(PasswordValidationResult.Success);

    public static FakeUserProvisioningService ThatRejects(params string[] errorCodes) =>
        new(PasswordValidationResult.Failure(errorCodes));

    public int ValidatePasswordCallCount { get; private set; }
    public int HashPasswordCallCount { get; private set; }
    public string? LastHashedPassword { get; private set; }

    public Task<PasswordValidationResult> ValidatePasswordAsync(string password)
    {
        ValidatePasswordCallCount++;
        return Task.FromResult(_validationResult);
    }

    public PasswordHash HashPassword(string password)
    {
        HashPasswordCallCount++;
        LastHashedPassword = password;
        return PasswordHash.FromEncoded($"hashed:{password}");
    }
}
