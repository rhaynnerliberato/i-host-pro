using FluentAssertions;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Infrastructure.Sessions;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Sessions;

public class UserSessionRevokerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static Session CreateActiveSession() =>
        Session.Open(Guid.NewGuid(), TenantId, UserId, Now.AddMinutes(-5), "Android", "Chrome", "203.0.113.9");

    private static RefreshToken CreateActiveToken(Guid sessionId) =>
        RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), TenantId, sessionId, UserId, "hash", Now.AddMinutes(-5), Now.AddDays(30));

    [Fact]
    public async Task Zero_active_sessions_revokes_nothing_and_signals_nothing()
    {
        var sessionReader = FakeSessionReader.WithSessions();
        var refreshTokenReader = FakeRefreshTokenReader.Empty();
        var signal = new FakeSessionRevocationSignal();
        var revoker = new UserSessionRevoker(sessionReader, refreshTokenReader, signal);

        var revokedSessionIds = await revoker.RevokeAllActiveSessionsAsync(
            TenantId, UserId, SessionRevokedReasonCodes.RolesChanged, RefreshTokenRevocationReason.RolesChanged,
            Now, CancellationToken.None);

        revokedSessionIds.Should().BeEmpty();
        signal.MarkedRevocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_active_session_with_no_active_refresh_tokens_is_still_revoked()
    {
        var session = CreateActiveSession();
        var sessionReader = FakeSessionReader.WithSessions(session);
        var refreshTokenReader = FakeRefreshTokenReader.Empty();
        var signal = new FakeSessionRevocationSignal();
        var revoker = new UserSessionRevoker(sessionReader, refreshTokenReader, signal);

        var revokedSessionIds = await revoker.RevokeAllActiveSessionsAsync(
            TenantId, UserId, SessionRevokedReasonCodes.RolesChanged, RefreshTokenRevocationReason.RolesChanged,
            Now, CancellationToken.None);

        revokedSessionIds.Should().Equal(session.Id);
        session.Status.Should().Be(SessionStatus.Revoked);
        session.RevocationReason.Should().Be(SessionRevokedReasonCodes.RolesChanged);
        signal.MarkedRevocations.Should().Equal((TenantId, session.Id));
    }

    [Fact]
    public async Task A_session_with_active_refresh_tokens_revokes_every_one_of_them()
    {
        var session = CreateActiveSession();
        var tokenA = CreateActiveToken(session.Id);
        var tokenB = CreateActiveToken(session.Id);
        var sessionReader = FakeSessionReader.WithSessions(session);
        var refreshTokenReader = FakeRefreshTokenReader.WithActiveTokens(session.Id, tokenA, tokenB);
        var signal = new FakeSessionRevocationSignal();
        var revoker = new UserSessionRevoker(sessionReader, refreshTokenReader, signal);

        await revoker.RevokeAllActiveSessionsAsync(
            TenantId, UserId, SessionRevokedReasonCodes.RolesChanged, RefreshTokenRevocationReason.RolesChanged,
            Now, CancellationToken.None);

        tokenA.IsRevoked.Should().BeTrue();
        tokenA.RevocationReason.Should().Be(RefreshTokenRevocationReason.RolesChanged);
        tokenB.IsRevoked.Should().BeTrue();
        tokenB.RevocationReason.Should().Be(RefreshTokenRevocationReason.RolesChanged);
    }

    [Fact]
    public async Task Multiple_active_sessions_are_all_revoked_and_each_signaled_exactly_once()
    {
        var sessionA = CreateActiveSession();
        var sessionB = CreateActiveSession();
        var sessionReader = FakeSessionReader.WithSessions(sessionA, sessionB);
        var refreshTokenReader = FakeRefreshTokenReader.Empty();
        var signal = new FakeSessionRevocationSignal();
        var revoker = new UserSessionRevoker(sessionReader, refreshTokenReader, signal);

        var revokedSessionIds = await revoker.RevokeAllActiveSessionsAsync(
            TenantId, UserId, SessionRevokedReasonCodes.RolesChanged, RefreshTokenRevocationReason.RolesChanged,
            Now, CancellationToken.None);

        revokedSessionIds.Should().BeEquivalentTo([sessionA.Id, sessionB.Id]);
        sessionA.Status.Should().Be(SessionStatus.Revoked);
        sessionB.Status.Should().Be(SessionStatus.Revoked);
        signal.MarkedRevocations.Should().BeEquivalentTo([(TenantId, sessionA.Id), (TenantId, sessionB.Id)]);
    }

    [Fact]
    public async Task The_cancellation_token_is_forwarded_to_the_session_reader()
    {
        var sessionReader = FakeSessionReader.WithSessions();
        var refreshTokenReader = FakeRefreshTokenReader.Empty();
        var signal = new FakeSessionRevocationSignal();
        var revoker = new UserSessionRevoker(sessionReader, refreshTokenReader, signal);
        using var cts = new CancellationTokenSource();

        await revoker.RevokeAllActiveSessionsAsync(
            TenantId, UserId, SessionRevokedReasonCodes.RolesChanged, RefreshTokenRevocationReason.RolesChanged,
            Now, cts.Token);

        sessionReader.LastCancellationToken.Should().Be(cts.Token);
    }
}
