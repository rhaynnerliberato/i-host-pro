using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Signup;

/// <inheritdoc cref="ISignupProcessor"/>
/// <remarks>
/// Reuses the exact domain factories <c>TenantProvisioner</c> (the operator
/// CLI) already uses, including its RLS-bootstrap sequence (<see cref="ITenantContext.SetTenant"/>
/// then <c>SET LOCAL app.tenant_id</c> — needed because this method
/// generates its OWN new tenant id and no ambient tenant context exists
/// yet). Deliberately does NOT reuse that tool's "existing slug -&gt; attach
/// to that tenant" branch: a slug collision here always retries with a
/// different candidate, since a stranger must never join an existing tenant
/// by guessing its name.
/// </remarks>
public sealed class SignupProcessor : ISignupProcessor
{
    private static readonly Error SlugGenerationFailedError = new("Identity.SignupSlugGenerationFailed", "Identity.SignupSlugGenerationFailed");
    private const int MaxSlugAttempts = 5;
    private const int MaxDeviceLength = 200;
    private const int MaxBrowserLength = 200;

    private readonly IdentityDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly IUserProvisioningService _provisioningService;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IRepository<Session, Guid> _sessionRepository;
    private readonly IRepository<RefreshToken, Guid> _refreshTokenRepository;
    private readonly IRefreshTokenGenerator _refreshTokenGenerator;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly TimeProvider _timeProvider;

    public SignupProcessor(
        IdentityDbContext dbContext,
        ITenantContext tenantContext,
        IIdentityTransactionExecutor transactionExecutor,
        IUserProvisioningService provisioningService,
        ISecurityAuditWriter auditWriter,
        IRepository<Session, Guid> sessionRepository,
        IRepository<RefreshToken, Guid> refreshTokenRepository,
        IRefreshTokenGenerator refreshTokenGenerator,
        IJwtTokenGenerator jwtTokenGenerator,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _transactionExecutor = transactionExecutor;
        _provisioningService = provisioningService;
        _auditWriter = auditWriter;
        _sessionRepository = sessionRepository;
        _refreshTokenRepository = refreshTokenRepository;
        _refreshTokenGenerator = refreshTokenGenerator;
        _jwtTokenGenerator = jwtTokenGenerator;
        _timeProvider = timeProvider;
    }

    public async Task<Result<SignupResult>> ProcessAsync(
        string companyName, string adminFullName, string adminEmail, string password, CancellationToken cancellationToken)
    {
        // Stateless — checked before any tenant/user row is created, so a
        // fixable client error (weak password) never leaves an orphan tenant.
        var passwordCheck = await _provisioningService.ValidatePasswordAsync(password);
        if (!passwordCheck.Succeeded)
            return Result.Failure<SignupResult>(new Error(string.Join(",", passwordCheck.ErrorCodes), "password_policy_violation"));

        var email = Email.Create(adminEmail);
        var tenantId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();

        // No ambient tenant context exists yet — this call generates a BRAND
        // NEW tenant id, so (unlike login/refresh) there is nothing to
        // resolve; SetTenant/SET LOCAL bootstrap this new id directly,
        // mirroring TenantProvisioner/DevelopmentIdentitySeeder exactly.
        _tenantContext.SetTenant(tenantId);
#pragma warning disable EF1002
        await _dbContext.Database.ExecuteSqlRawAsync($"SET LOCAL app.tenant_id = '{tenantId:D}'", cancellationToken);
#pragma warning restore EF1002

        var slug = await GenerateUniqueSlugAsync(companyName, cancellationToken);
        if (slug is null)
            return Result.Failure<SignupResult>(SlugGenerationFailedError);

        return await _transactionExecutor.ExecuteAsync(async () =>
        {
            var tenant = Tenant.Provision(tenantId, slug, companyName, now);
            _dbContext.Tenants.Add(tenant);

            var passwordHash = _provisioningService.HashPassword(password);
            var user = User.Register(Guid.NewGuid(), tenantId, email, adminFullName, passwordHash, now);
            _dbContext.Users.Add(user);
            _dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, "ADMIN", now, assignedByUserId: null));

            var correlationId = Guid.NewGuid();
            _auditWriter.Record(SecurityAuditEntry.Record(
                Guid.NewGuid(), tenantId, SecurityAuditEventType.UserCreated, now, correlationId,
                reasonCode: null, userId: user.Id, sessionId: null, refreshTokenId: null, ipAddress: null));
            _auditWriter.Record(SecurityAuditEntry.Record(
                Guid.NewGuid(), tenantId, SecurityAuditEventType.UserRoleAssigned, now, correlationId,
                reasonCode: null, userId: user.Id, sessionId: null, refreshTokenId: null, ipAddress: null));

            user.RecordSuccessfulLogin(now);

            var session = Session.Open(Guid.NewGuid(), tenantId, user.Id, now, device: null, browser: null, ipAddress: null);
            _sessionRepository.Add(session);

            var generatedRefreshToken = _refreshTokenGenerator.Generate(tenantId);
            var refreshToken = RefreshToken.Issue(
                Guid.NewGuid(), generatedRefreshToken.TokenId, tenantId, session.Id, user.Id,
                generatedRefreshToken.TokenHash, now, generatedRefreshToken.ExpiresAt);
            _refreshTokenRepository.Add(refreshToken);

            // The role just assigned above is known with certainty here — no
            // re-query needed (and none would reliably see the uncommitted
            // UserRole insert before this transaction's single SaveChanges).
            var accessToken = _jwtTokenGenerator.GenerateAccessToken(
                new JwtAccessTokenRequest(user.Id, tenantId, session.Id, ["ADMIN"]));

            var tokens = new AuthTokensResult(
                accessToken.Token, accessToken.ExpiresAt, generatedRefreshToken.Token, generatedRefreshToken.ExpiresAt);

            return Result.Success(new SignupResult(slug.Value, tokens));
        }, cancellationToken);
    }

    /// <summary>
    /// Normalizes <paramref name="companyName"/> into a candidate slug and
    /// retries with a short random suffix on collision — NEVER attaches to
    /// the colliding tenant (unlike <c>TenantProvisioner</c>'s own
    /// lookup-then-branch, which is correct only for its operator-driven use
    /// case). The `tenants` table carries no RLS (Incremento 1 plan), so this
    /// lookup needs no tenant context.
    /// </summary>
    private async Task<TenantSlug?> GenerateUniqueSlugAsync(string companyName, CancellationToken cancellationToken)
    {
        var baseSlug = Normalize(companyName);

        for (var attempt = 0; attempt < MaxSlugAttempts; attempt++)
        {
            var candidate = attempt == 0 ? baseSlug : $"{baseSlug}-{RandomSuffix()}";
            var slug = TenantSlug.Create(candidate);

            var exists = await _dbContext.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken);
            if (!exists)
                return slug;
        }

        return null;
    }

    private static string Normalize(string companyName)
    {
        var decomposed = companyName.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > 50)
            slug = slug[..50].Trim('-');
        if (slug.Length < 3)
            slug = $"tenant-{slug}".PadRight(3, '0');

        return slug;
    }

    private static string RandomSuffix() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
}
