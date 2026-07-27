using FluentAssertions;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Tests.Unit.Domain;

public class SessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Open_creates_an_active_session()
    {
        var session = Session.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, "iPhone", "Safari", "203.0.113.1");

        session.Status.Should().Be(SessionStatus.Active);
        session.LastActivityAt.Should().Be(Now);
    }

    [Fact]
    public void Touch_on_a_revoked_session_throws()
    {
        var session = Session.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, null, null, null);
        session.Revoke("LogoutRequested", Now);

        var act = () => session.Touch(Now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Revoke_is_idempotent()
    {
        var session = Session.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, null, null, null);

        session.Revoke("LogoutRequested", Now);
        session.Revoke("ReuseDetected", Now.AddSeconds(1));

        session.RevocationReason.Should().Be("LogoutRequested");
    }
}

public class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static RefreshToken CreateToken() => RefreshToken.Issue(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        tokenHash: new string('a', 64), issuedAt: Now, expiresAt: Now.AddDays(30));

    [Fact]
    public void Issue_rejects_expiration_not_after_issuance()
    {
        var act = () => RefreshToken.Issue(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            tokenHash: "hash", issuedAt: Now, expiresAt: Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsExpired_reflects_the_configured_expiry()
    {
        var token = CreateToken();

        token.IsExpired(Now.AddDays(29)).Should().BeFalse();
        token.IsExpired(Now.AddDays(31)).Should().BeTrue();
    }

    [Fact]
    public void MarkRotated_sets_revocation_fields_and_links_successor()
    {
        var token = CreateToken();
        var successorId = Guid.NewGuid();

        token.MarkRotated(successorId, Now.AddHours(1));

        token.IsRevoked.Should().BeTrue();
        token.RevocationReason.Should().Be(RefreshTokenRevocationReason.Rotated);
        token.ReplacedByTokenId.Should().Be(successorId);
    }

    [Fact]
    public void MarkRotated_on_an_already_revoked_token_throws()
    {
        // This is precisely the guard that forces the caller to treat reuse of
        // an already-rotated token as a distinct security case, never as an
        // ordinary rotation (Incremento 1 plan, Section 7).
        var token = CreateToken();
        token.Revoke(RefreshTokenRevocationReason.LogoutRequested, Now);

        var act = () => token.MarkRotated(Guid.NewGuid(), Now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Revoke_is_idempotent_and_keeps_the_first_reason()
    {
        var token = CreateToken();

        token.Revoke(RefreshTokenRevocationReason.ReuseDetected, Now);
        token.Revoke(RefreshTokenRevocationReason.AdminRevoked, Now.AddSeconds(1));

        token.RevocationReason.Should().Be(RefreshTokenRevocationReason.ReuseDetected);
    }
}
