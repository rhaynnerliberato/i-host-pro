using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Only the ClientId-missing fast-fail path is safe to exercise here: a real
/// <see cref="Microsoft.Identity.Client.IPublicClientApplication.AcquireTokenInteractive"/>
/// call opens a real system browser and requires a human to complete
/// Microsoft sign-in — it cannot be automated in a unit (or any CI)
/// environment. Proving the real interactive/silent flow works is a manual
/// smoke step for a developer to run locally, not something this test suite
/// claims to cover.
/// </summary>
public class MsalAirbnbEmailAuthenticatorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static ExternalIntegrationsDbContext CreateUnusedDbContext()
    {
        var options = new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new ExternalIntegrationsDbContext(options, new TenantContext());
    }

    [Fact]
    public async Task ConnectInteractiveAsync_fails_fast_without_touching_the_database_when_ClientId_is_not_configured()
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var tokenCacheStore = new FakeAirbnbEmailTokenCacheStore(repository);
        await using var dbContext = CreateUnusedDbContext();
        var authenticator = new MsalAirbnbEmailAuthenticator(
            repository, tokenCacheStore, dbContext, Options.Create(new AirbnbEmailBridgeOptions { ClientId = null }),
            TimeProvider.System, NullLogger<MsalAirbnbEmailAuthenticator>.Instance);

        var outcome = await authenticator.ConnectInteractiveAsync(TenantId, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.FailureReason.Should().Be(AirbnbEmailAuthenticationFailureReason.AcquisitionFailed);
        repository.Current.Should().BeNull("a missing ClientId must fail before any connection row is created");
    }
}
