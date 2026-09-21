namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Handles an anonymous forgot-password request (Self-Service Identity
/// &amp; Onboarding Foundation gate). Implemented in Infrastructure (needs
/// <c>ITenantContext</c>) and called DIRECTLY by the Api controller,
/// bypassing <see cref="IIdentityRequestDispatcher"/> entirely — mirrors
/// exactly why the Airbnb Email Bridge's Web OAuth callback processor bypasses
/// its own dispatcher: every ordinary pipeline behavior assumes a tenant is
/// already resolved, which is never true here before the tenant slug is
/// looked up.
///
/// Always completes without throwing for a normal "unknown tenant/email"
/// case — the caller (controller) must return the exact same generic
/// response regardless of what actually happened, to avoid account/tenant
/// enumeration. This method itself does the "same regardless" part
/// internally; it has no success/failure return value on purpose.
/// </summary>
public interface IStartPasswordResetProcessor
{
    Task ProcessAsync(string tenantSlug, string email, CancellationToken cancellationToken);
}
