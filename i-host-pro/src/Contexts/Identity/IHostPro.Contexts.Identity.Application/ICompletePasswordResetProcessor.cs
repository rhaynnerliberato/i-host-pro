using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Completes an anonymous forgot-password request (Self-Service Identity
/// &amp; Onboarding Foundation gate). Implemented in Infrastructure (needs
/// <c>ITenantContext</c>) and called DIRECTLY by the Api controller,
/// bypassing <see cref="IIdentityRequestDispatcher"/> — mirrors
/// <c>IAirbnbEmailWebOAuthCallbackProcessor</c>'s exact reason: the tenant is
/// not trustworthy until the token itself has been atomically consumed.
///
/// The new password's policy is validated BEFORE the token is consumed
/// (policy validation is stateless — never needs a resolved tenant/user), so
/// a caller who fails policy can retry with the SAME token/link instead of
/// losing it. Every other failure (invalid/expired/already-consumed token)
/// returns the same <see cref="IHostPro.Contexts.Identity.Application.Errors.IdentityErrorCodes.PasswordResetTokenInvalid"/>
/// — never distinguished in the response.
/// </summary>
public interface ICompletePasswordResetProcessor
{
    Task<Result> ProcessAsync(string token, string newPassword, CancellationToken cancellationToken);
}
