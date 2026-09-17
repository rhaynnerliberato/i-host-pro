using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Proves the callback bootstrap sequence in isolation (Web OAuth
/// architecture gate, items 13-18) — the real-Postgres proof that
/// <see cref="IAirbnbEmailOAuthTransactionRepository.ConsumeByStateHashAsync"/>
/// itself works with no ambient tenant context lives in
/// <c>ExternalIntegrationsFoundationTests</c>; this suite proves the
/// PROCESSOR's own orchestration/decision logic around it.
/// </summary>
public class AirbnbEmailWebOAuthCallbackProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private const string FrontendReturnUrl = "http://localhost:4200/integrations/airbnb-email";

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AirbnbEmailWebOAuthCallbackProcessor CreateProcessor(
        FakeAirbnbEmailOAuthTransactionRepository transactionRepository,
        FakeAirbnbEmailWebOAuthAuthenticator authenticator,
        ITenantContext tenantContext,
        string? webFrontendReturnUrl = FrontendReturnUrl) =>
        new(transactionRepository, authenticator, tenantContext,
            Options.Create(new AirbnbEmailBridgeOptions { WebFrontendReturnUrl = webFrontendReturnUrl }),
            new FixedTimeProvider(Now), NullLogger<AirbnbEmailWebOAuthCallbackProcessor>.Instance);

    [Fact]
    public async Task A_microsoft_error_query_parameter_returns_UserCancelledOrConsentDenied_without_touching_the_repository()
    {
        var transactionRepository = new FakeAirbnbEmailOAuthTransactionRepository();
        var tenantContext = new TenantContext();
        var processor = CreateProcessor(transactionRepository, FakeAirbnbEmailWebOAuthAuthenticator.NotConfigured(), tenantContext);

        var report = await processor.ProcessAsync(state: "any", authorizationCode: null, microsoftError: "access_denied", CancellationToken.None);

        report.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.UserCancelledOrConsentDenied);
        report.FrontendReturnUrl.Should().Be(FrontendReturnUrl);
        tenantContext.IsResolved.Should().BeFalse("a Microsoft error must never establish a tenant context");
    }

    [Theory]
    [InlineData(null, "code-1")]
    [InlineData("state-1", null)]
    [InlineData("", "")]
    public async Task Missing_state_or_code_returns_InvalidOrExpiredState(string? state, string? code)
    {
        var tenantContext = new TenantContext();
        var processor = CreateProcessor(
            new FakeAirbnbEmailOAuthTransactionRepository(), FakeAirbnbEmailWebOAuthAuthenticator.NotConfigured(), tenantContext);

        var report = await processor.ProcessAsync(state, code, microsoftError: null, CancellationToken.None);

        report.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.InvalidOrExpiredState);
        tenantContext.IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_state_returns_InvalidOrExpiredState_and_never_sets_a_tenant()
    {
        var tenantContext = new TenantContext();
        var processor = CreateProcessor(
            new FakeAirbnbEmailOAuthTransactionRepository(), FakeAirbnbEmailWebOAuthAuthenticator.NotConfigured(), tenantContext);

        var report = await processor.ProcessAsync("never-created-state", "some-code", microsoftError: null, CancellationToken.None);

        report.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.InvalidOrExpiredState);
        tenantContext.IsResolved.Should().BeFalse("an invalid state must never establish a trusted tenant");
    }

    [Fact]
    public async Task A_valid_state_establishes_the_trusted_tenant_ONLY_after_consuming_it_and_completes_the_exchange()
    {
        var transactionRepository = new FakeAirbnbEmailOAuthTransactionRepository();
        var protectedVerifier = new byte[] { 1, 2, 3 };
        transactionRepository.CreatePending(Guid.NewGuid(), TenantId, ActorUserId, AirbnbEmailOAuthStateHasher.Hash("real-state"), protectedVerifier, Now, Now.AddMinutes(10));
        var authenticator = FakeAirbnbEmailWebOAuthAuthenticator.CompletingWith(AirbnbEmailWebOAuthCallbackOutcome.Success());
        var tenantContext = new TenantContext();
        var processor = CreateProcessor(transactionRepository, authenticator, tenantContext);

        var report = await processor.ProcessAsync("real-state", "auth-code-1", microsoftError: null, CancellationToken.None);

        report.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.Success);
        tenantContext.IsResolved.Should().BeTrue();
        tenantContext.TenantId.Should().Be(TenantId, "the tenant must come exclusively from the consumed transaction row");

        authenticator.LastCompleteCall.Should().NotBeNull();
        authenticator.LastCompleteCall!.Value.TenantId.Should().Be(TenantId);
        authenticator.LastCompleteCall.Value.AuthorizationCode.Should().Be("auth-code-1");
        authenticator.LastCompleteCall.Value.ProtectedPkceVerifier.Should().Equal(protectedVerifier);
    }

    [Fact]
    public async Task A_replayed_state_the_second_call_returns_InvalidOrExpiredState()
    {
        var transactionRepository = new FakeAirbnbEmailOAuthTransactionRepository();
        transactionRepository.CreatePending(Guid.NewGuid(), TenantId, ActorUserId, AirbnbEmailOAuthStateHasher.Hash("reused-state"), [1], Now, Now.AddMinutes(10));
        var authenticator = FakeAirbnbEmailWebOAuthAuthenticator.CompletingWith(AirbnbEmailWebOAuthCallbackOutcome.Success());
        var processor = CreateProcessor(transactionRepository, authenticator, new TenantContext());

        var first = await processor.ProcessAsync("reused-state", "code", microsoftError: null, CancellationToken.None);
        var second = await processor.ProcessAsync("reused-state", "code", microsoftError: null, CancellationToken.None);

        first.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.Success);
        second.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.InvalidOrExpiredState);
    }

    [Fact]
    public async Task Consent_denied_during_the_exchange_maps_to_UserCancelledOrConsentDenied()
    {
        var transactionRepository = new FakeAirbnbEmailOAuthTransactionRepository();
        transactionRepository.CreatePending(Guid.NewGuid(), TenantId, ActorUserId, AirbnbEmailOAuthStateHasher.Hash("state-x"), [1], Now, Now.AddMinutes(10));
        var authenticator = FakeAirbnbEmailWebOAuthAuthenticator.CompletingWith(
            AirbnbEmailWebOAuthCallbackOutcome.Failure(AirbnbEmailWebOAuthCallbackFailureReason.ConsentDenied));
        var processor = CreateProcessor(transactionRepository, authenticator, new TenantContext());

        var report = await processor.ProcessAsync("state-x", "code", microsoftError: null, CancellationToken.None);

        report.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.UserCancelledOrConsentDenied);
    }

    [Theory]
    [InlineData(AirbnbEmailWebOAuthCallbackFailureReason.ExchangeFailed)]
    [InlineData(AirbnbEmailWebOAuthCallbackFailureReason.AccountIdentityUnavailable)]
    public async Task Exchange_or_account_identity_failures_map_to_ExchangeFailed(AirbnbEmailWebOAuthCallbackFailureReason reason)
    {
        var transactionRepository = new FakeAirbnbEmailOAuthTransactionRepository();
        transactionRepository.CreatePending(Guid.NewGuid(), TenantId, ActorUserId, AirbnbEmailOAuthStateHasher.Hash("state-y"), [1], Now, Now.AddMinutes(10));
        var authenticator = FakeAirbnbEmailWebOAuthAuthenticator.CompletingWith(AirbnbEmailWebOAuthCallbackOutcome.Failure(reason));
        var processor = CreateProcessor(transactionRepository, authenticator, new TenantContext());

        var report = await processor.ProcessAsync("state-y", "code", microsoftError: null, CancellationToken.None);

        report.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.ExchangeFailed);
    }

    [Fact]
    public async Task Returns_NotConfigured_with_a_null_frontend_url_when_WebFrontendReturnUrl_is_unset()
    {
        var processor = CreateProcessor(
            new FakeAirbnbEmailOAuthTransactionRepository(), FakeAirbnbEmailWebOAuthAuthenticator.NotConfigured(),
            new TenantContext(), webFrontendReturnUrl: null);

        var report = await processor.ProcessAsync("any-state", "any-code", microsoftError: null, CancellationToken.None);

        report.Result.Should().Be(AirbnbEmailWebOAuthCallbackResult.NotConfigured);
        report.FrontendReturnUrl.Should().BeNull();
    }
}
