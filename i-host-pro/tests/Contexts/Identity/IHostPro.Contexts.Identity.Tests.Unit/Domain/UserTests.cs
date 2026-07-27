using FluentAssertions;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Tests.Unit.Domain;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static User CreateUser() => User.Register(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Email.Create("admin@ihostpro.com"),
        "Admin User",
        PasswordHash.FromEncoded("$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2g"),
        Now);

    [Fact]
    public void Register_sets_active_status_and_derived_normalized_email()
    {
        var user = CreateUser();

        user.Status.Should().Be(UserStatus.Active);
        user.NormalizedEmail.Should().Be(EmailNormalizer.Normalize("admin@ihostpro.com"));
        user.FailedAccessCount.Should().Be(0);
    }

    [Fact]
    public void Register_rejects_empty_full_name()
    {
        var act = () => User.Register(
            Guid.NewGuid(), Guid.NewGuid(), Email.Create("a@b.com"), "   ",
            PasswordHash.FromEncoded("x"), Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ChangeEmail_keeps_NormalizedEmail_in_sync_with_Email()
    {
        var user = CreateUser();

        user.ChangeEmail(Email.Create("New.Address@IHostPro.com"), Now);

        user.Email.Value.Should().Be("New.Address@IHostPro.com");
        user.NormalizedEmail.Should().Be(user.Email.NormalizedValue);
        user.NormalizedEmail.Should().Be("new.address@ihostpro.com");
    }

    [Fact]
    public void SetPasswordHash_rotates_security_stamp()
    {
        var user = CreateUser();
        var previousStamp = user.SecurityStamp;

        user.SetPasswordHash(PasswordHash.FromEncoded("$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aGFzaA"), Now);

        user.SecurityStamp.Should().NotBe(previousStamp);
    }

    [Fact]
    public void Block_rotates_security_stamp_and_sets_status()
    {
        var user = CreateUser();
        var previousStamp = user.SecurityStamp;

        user.Block(Now);

        user.Status.Should().Be(UserStatus.Blocked);
        user.SecurityStamp.Should().NotBe(previousStamp);
    }

    [Fact]
    public void IncrementFailedAccessCount_increments_and_ResetFailedAccessCount_resets()
    {
        var user = CreateUser();

        user.IncrementFailedAccessCount(Now);
        user.IncrementFailedAccessCount(Now);
        user.FailedAccessCount.Should().Be(2);

        user.ResetFailedAccessCount(Now);
        user.FailedAccessCount.Should().Be(0);
    }

    [Fact]
    public void LockUntil_sets_LockoutEnd_and_ClearLockout_clears_it()
    {
        var user = CreateUser();
        var until = Now.AddMinutes(15);

        user.LockUntil(until, Now);
        user.LockoutEnd.Should().Be(until);

        user.ClearLockout(Now);
        user.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public void RecordSuccessfulLogin_sets_LastLoginAt()
    {
        var user = CreateUser();

        user.RecordSuccessfulLogin(Now);

        user.LastLoginAt.Should().Be(Now);
    }
}
