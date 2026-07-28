using IHostPro.BuildingBlocks.Application;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Resolves the tenant for a <see cref="RefreshTokenCommand"/> from the
/// non-sensitive tenant-id segment embedded in the presented token's wire
/// format (Incremento 2 plan, Etapa 10) — same structure as
/// <see cref="LoginTenantBootstrapResolver"/>.
///
/// The token is strictly parsed <b>before</b> any tenant lookup — a
/// malformed token never reaches <see cref="ITenantBootstrapReader"/> at
/// all. Neither a parse failure nor an unresolved/inactive tenant is ever
/// persisted to `security_audit_log`: no tenant is validly resolved yet to
/// scope the entry under (<see cref="Domain.SecurityAuditEntry"/> is only
/// ever created for an already-resolved tenant). Both cases are recorded
/// exclusively via structured logging/telemetry, and the caller always sees
/// the exact same generic <c>Tenant.NotFound</c> failure
/// (<see cref="BuildingBlocks.Infrastructure.Persistence.TenantBootstrapBehavior{TMessage,TResponse}"/>)
/// regardless of which of the two occurred.
/// </summary>
public sealed class RefreshTokenTenantBootstrapResolver : ITenantBootstrapResolver<RefreshTokenCommand>
{
    private readonly IRefreshTokenParser _parser;
    private readonly ITenantBootstrapReader _tenantBootstrapReader;
    private readonly ILogger<RefreshTokenTenantBootstrapResolver> _logger;

    public RefreshTokenTenantBootstrapResolver(
        IRefreshTokenParser parser,
        ITenantBootstrapReader tenantBootstrapReader,
        ILogger<RefreshTokenTenantBootstrapResolver> logger)
    {
        _parser = parser;
        _tenantBootstrapReader = tenantBootstrapReader;
        _logger = logger;
    }

    public async Task<Guid?> ResolveTenantAsync(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        if (!_parser.TryParse(request.RefreshToken, out var parsed))
        {
            _logger.LogWarning("Refresh token exchange attempted with a malformed token.");
            return null;
        }

        var tenant = await _tenantBootstrapReader.GetActiveTenantByIdAsync(parsed.TenantId, cancellationToken);
        if (tenant is not null)
            return tenant.Id;

        _logger.LogWarning(
            "Refresh token exchange attempted for an unresolved or inactive tenant {TenantId}", parsed.TenantId);
        return null;
    }
}
