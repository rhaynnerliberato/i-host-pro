using IHostPro.BuildingBlocks.Application;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Identity.Application;

/// <inheritdoc cref="ITenantBootstrapResolver{TRequest}"/>
/// <remarks>
/// Resolves the tenant by slug for <see cref="LoginCommand"/> (Incremento 2
/// plan, Etapa 9). An unresolved/inactive tenant is reported only via
/// structured logging (<see cref="ILogger"/>) — never written to
/// <c>security_audit_log</c>, which is tenant-owned/RLS-protected and has no
/// tenant to be scoped under at this point (see <c>SecurityAuditEntry</c>).
///
/// Also runs the dummy Argon2id verification here, not only in
/// <see cref="LoginCommandHandler"/>: without it, an unresolved tenant would
/// fail in microseconds while a resolved tenant with a rejected user/password
/// costs a full Argon2id computation, itself an observable timing oracle for
/// "is this tenant slug valid" (Incremento 2 plan, Etapa 9 timing analysis).
///
/// Never logs <see cref="LoginCommand.Password"/> — only the (non-sensitive)
/// tenant slug that was attempted.
/// </remarks>
public sealed class LoginTenantBootstrapResolver : ITenantBootstrapResolver<LoginCommand>
{
    private readonly ITenantBootstrapReader _tenantBootstrapReader;
    private readonly IDummyPasswordVerifier _dummyPasswordVerifier;
    private readonly ILogger<LoginTenantBootstrapResolver> _logger;

    public LoginTenantBootstrapResolver(
        ITenantBootstrapReader tenantBootstrapReader,
        IDummyPasswordVerifier dummyPasswordVerifier,
        ILogger<LoginTenantBootstrapResolver> logger)
    {
        _tenantBootstrapReader = tenantBootstrapReader;
        _dummyPasswordVerifier = dummyPasswordVerifier;
        _logger = logger;
    }

    public async Task<Guid?> ResolveTenantAsync(LoginCommand request, CancellationToken cancellationToken)
    {
        var tenant = await _tenantBootstrapReader.GetActiveTenantBySlugAsync(request.TenantSlug, cancellationToken);

        if (tenant is not null)
            return tenant.Id;

        _dummyPasswordVerifier.Verify(request.Password);

        _logger.LogWarning(
            "Login attempt for an unresolved or inactive tenant slug {TenantSlug}", request.TenantSlug);

        return null;
    }
}
