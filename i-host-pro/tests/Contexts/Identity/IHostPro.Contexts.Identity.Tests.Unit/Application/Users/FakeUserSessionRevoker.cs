using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeUserSessionRevoker : IUserSessionRevoker
{
    private readonly IReadOnlyCollection<Guid> _revokedSessionIds;

    private FakeUserSessionRevoker(IReadOnlyCollection<Guid> revokedSessionIds) => _revokedSessionIds = revokedSessionIds;

    public static FakeUserSessionRevoker ThatRevokes(params Guid[] sessionIds) => new(sessionIds);

    public static FakeUserSessionRevoker ThatRevokesNone() => new([]);

    public int CallCount { get; private set; }
    public Guid? LastTenantId { get; private set; }
    public Guid? LastUserId { get; private set; }
    public string? LastSessionRevocationReasonCode { get; private set; }
    public RefreshTokenRevocationReason? LastRefreshTokenRevocationReason { get; private set; }

    public Task<IReadOnlyCollection<Guid>> RevokeAllActiveSessionsAsync(
        Guid tenantId,
        Guid userId,
        string sessionRevocationReasonCode,
        RefreshTokenRevocationReason refreshTokenRevocationReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastTenantId = tenantId;
        LastUserId = userId;
        LastSessionRevocationReasonCode = sessionRevocationReasonCode;
        LastRefreshTokenRevocationReason = refreshTokenRevocationReason;
        return Task.FromResult(_revokedSessionIds);
    }
}
