using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.PasswordReset;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure.PasswordReset;

/// <summary>
/// Focused on the one behavior this gate changes: a transactional-email
/// provider failure (e.g. Resend outage) must never propagate out of the
/// forgot-password flow — the token is already durably persisted by that
/// point, and leaking a delivery failure as a distinct response would be an
/// account-enumeration/availability signal the existing anti-enumeration
/// design (Architecture + Security Design Gate, item 30) must not have.
/// </summary>
public class StartPasswordResetProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProcessAsync_does_not_throw_when_the_email_sender_fails()
    {
        var user = User.Register(Guid.NewGuid(), TenantId, Email.Create("guest@ihostpro.com"), "Guest User", PasswordHash.FromEncoded("irrelevant-for-this-test"), Now);
        var tenantContext = new TenantContext();

        var processor = new StartPasswordResetProcessor(
            new FakeTenantBootstrapReader(new ActiveTenant(TenantId, "acme")),
            tenantContext,
            new PassThroughIdentityTransactionExecutor(),
            FakeUserAuthenticationServiceForReset.WithUser(user),
            new FakePasswordResetTokenRepository(),
            new NoOpSecurityAuditWriter(),
            new ThrowingTransactionalEmailSender(),
            Options.Create(new PasswordResetOptions()),
            TimeProvider.System,
            NullLogger<StartPasswordResetProcessor>.Instance);

        var act = () => processor.ProcessAsync("acme", "guest@ihostpro.com", CancellationToken.None);

        await act.Should().NotThrowAsync("a provider outage must never surface as a different response than the normal, always-silent forgot-password outcome");
    }

    [Fact]
    public async Task ProcessAsync_sends_the_reset_email_with_the_resolved_users_address_when_the_sender_succeeds()
    {
        var user = User.Register(Guid.NewGuid(), TenantId, Email.Create("guest@ihostpro.com"), "Guest User", PasswordHash.FromEncoded("irrelevant-for-this-test"), Now);
        var tenantContext = new TenantContext();
        var sender = new RecordingTransactionalEmailSender();

        var processor = new StartPasswordResetProcessor(
            new FakeTenantBootstrapReader(new ActiveTenant(TenantId, "acme")),
            tenantContext,
            new PassThroughIdentityTransactionExecutor(),
            FakeUserAuthenticationServiceForReset.WithUser(user),
            new FakePasswordResetTokenRepository(),
            new NoOpSecurityAuditWriter(),
            sender,
            Options.Create(new PasswordResetOptions()),
            TimeProvider.System,
            NullLogger<StartPasswordResetProcessor>.Instance);

        await processor.ProcessAsync("acme", "guest@ihostpro.com", CancellationToken.None);

        sender.SentMessages.Should().ContainSingle();
        sender.SentMessages[0].ToAddress.Should().Be("guest@ihostpro.com");
        sender.SentMessages[0].PlainTextBody.Should().Contain("token=");
    }

    private sealed class FakeTenantBootstrapReader(ActiveTenant tenant) : ITenantBootstrapReader
    {
        public Task<ActiveTenant?> GetActiveTenantBySlugAsync(string slug, CancellationToken cancellationToken) =>
            Task.FromResult(slug == tenant.Slug ? tenant : null);

        public Task<ActiveTenant?> GetActiveTenantByIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == tenant.Id ? tenant : null);
    }

    private sealed class PassThroughIdentityTransactionExecutor : IIdentityTransactionExecutor
    {
        public Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken) =>
            operation();
    }

    private sealed class FakeUserAuthenticationServiceForReset(User user) : IUserAuthenticationService
    {
        public static FakeUserAuthenticationServiceForReset WithUser(User user) => new(user);

        public Task<User?> FindByIdAsync(Guid userId) => throw new NotSupportedException();
        public Task<User?> FindByEmailAsync(string email) => Task.FromResult(email == user.Email.Value ? user : null);
        public Task<bool> IsLockedOutAsync(User u) => throw new NotSupportedException();
        public Task<bool> CheckPasswordAsync(User u, string password) => throw new NotSupportedException();
        public Task AccessFailedAsync(User u) => throw new NotSupportedException();
        public Task ResetAccessFailedCountAsync(User u) => throw new NotSupportedException();
    }

    private sealed class FakePasswordResetTokenRepository : IPasswordResetTokenRepository
    {
        public void CreatePending(Guid id, Guid tenantId, Guid userId, string tokenHash, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
        {
        }

        public Task<PasswordResetTokenConsumption?> ConsumeByTokenHashAsync(string tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry)
        {
        }
    }

    private sealed class ThrowingTransactionalEmailSender : ITransactionalEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated Resend outage");
    }

    private sealed class RecordingTransactionalEmailSender : ITransactionalEmailSender
    {
        public List<EmailMessage> SentMessages { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }
    }
}
