using System.Security.Cryptography;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.PasswordReset;

/// <inheritdoc cref="IStartPasswordResetProcessor"/>
/// <remarks>
/// The ONE class in Identity that calls <see cref="ITenantContext.SetTenant"/>
/// for the forgot-password flow — only after the tenant slug has been
/// resolved against the non-RLS <c>tenants</c> table, mirroring
/// <c>LoginTenantBootstrapResolver</c>'s own lookup. Never writes to
/// <c>security_audit_log</c> for an unresolved tenant or a non-existent
/// email — there is either no tenant to scope the row under, or writing one
/// for a guessed email would itself be an account-enumeration side channel;
/// a structured log line is enough for operational visibility.
/// </remarks>
public sealed class StartPasswordResetProcessor : IStartPasswordResetProcessor
{
    private readonly ITenantBootstrapReader _tenantBootstrapReader;
    private readonly ITenantContext _tenantContext;
    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly IUserAuthenticationService _userAuthenticationService;
    private readonly IPasswordResetTokenRepository _tokenRepository;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly ITransactionalEmailSender _emailSender;
    private readonly IOptions<PasswordResetOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StartPasswordResetProcessor> _logger;

    public StartPasswordResetProcessor(
        ITenantBootstrapReader tenantBootstrapReader,
        ITenantContext tenantContext,
        IIdentityTransactionExecutor transactionExecutor,
        IUserAuthenticationService userAuthenticationService,
        IPasswordResetTokenRepository tokenRepository,
        ISecurityAuditWriter auditWriter,
        ITransactionalEmailSender emailSender,
        IOptions<PasswordResetOptions> options,
        TimeProvider timeProvider,
        ILogger<StartPasswordResetProcessor> logger)
    {
        _tenantBootstrapReader = tenantBootstrapReader;
        _tenantContext = tenantContext;
        _transactionExecutor = transactionExecutor;
        _userAuthenticationService = userAuthenticationService;
        _tokenRepository = tokenRepository;
        _auditWriter = auditWriter;
        _emailSender = emailSender;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ProcessAsync(string tenantSlug, string email, CancellationToken cancellationToken)
    {
        var tenant = await _tenantBootstrapReader.GetActiveTenantBySlugAsync(tenantSlug, cancellationToken);
        if (tenant is null)
        {
            _logger.LogWarning("Forgot-password request for an unresolved or inactive tenant slug {TenantSlug}.", tenantSlug);
            return;
        }

        _tenantContext.SetTenant(tenant.Id);

        var pending = await _transactionExecutor.ExecuteAsync(async () =>
        {
            var user = await _userAuthenticationService.FindByEmailAsync(email);
            if (user is null || user.Status != UserStatus.Active)
                return default((string Email, string FullName, string RawToken)?);

            var now = _timeProvider.GetUtcNow();
            var rawToken = GenerateUrlSafeRandomToken();
            var tokenHash = PasswordResetTokenHasher.Hash(rawToken);
            var lifetime = TimeSpan.FromMinutes(_options.Value.TokenLifetimeMinutes);

            _tokenRepository.CreatePending(Guid.NewGuid(), tenant.Id, user.Id, tokenHash, now, now.Add(lifetime));

            _auditWriter.Record(SecurityAuditEntry.Record(
                Guid.NewGuid(), tenant.Id, SecurityAuditEventType.PasswordResetRequested, now, Guid.NewGuid(),
                reasonCode: null, userId: user.Id, sessionId: null, refreshTokenId: null, ipAddress: null));

            return (user.Email.Value, user.FullName, rawToken);
        }, cancellationToken);

        if (pending is null)
        {
            _logger.LogInformation("Forgot-password request for tenant {TenantSlug} did not match an active account.", tenantSlug);
            return;
        }

        var resetUrl = $"{_options.Value.FrontendResetUrlBase}?token={pending.Value.RawToken}&tenant={tenant.Slug}";

        // The token is already durably persisted above - a delivery provider
        // failure here must never surface as a different outcome than the
        // normal, always-silent forgot-password response (that would itself
        // be an account-enumeration/availability side channel). Safe
        // metadata only is logged - never the token or the reset URL.
        try
        {
            await _emailSender.SendAsync(
                new EmailMessage(
                    pending.Value.Email,
                    pending.Value.FullName,
                    "Redefinição de senha — iHostPro",
                    $"Recebemos uma solicitação para redefinir sua senha. Se foi você, acesse o link abaixo " +
                    $"(válido por {_options.Value.TokenLifetimeMinutes} minutos):\n\n{resetUrl}\n\n" +
                    "Se você não solicitou isso, ignore este e-mail."),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password-reset email delivery failed for tenant {TenantSlug} - the reset token remains valid and usable.", tenantSlug);
        }
    }

    private static string GenerateUrlSafeRandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
