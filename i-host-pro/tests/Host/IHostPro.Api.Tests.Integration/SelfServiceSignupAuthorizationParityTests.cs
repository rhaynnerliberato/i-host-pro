using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using IHostPro.Contexts.Identity.Api.Contracts;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// UI/UX Visual Foundation gate's Authenticated Visual Runtime Verification
/// surfaced a real defect: an ADMIN created via the public self-service
/// <c>/api/v1/signup</c> endpoint received a 403 Forbidden from
/// <c>GET /api/v1/policies</c>, even though Documento 09 §15 ("Matriz
/// Simplificada") lists Admin as "X" (Controle total) for Políticas. Root
/// cause: <c>IdentityCatalogSeed</c> only ever granted ADMIN
/// <c>POLICIES:MANAGE</c>, never <c>POLICIES:READ</c> — and
/// <c>PoliciesController</c>'s GET endpoints require <c>POLICIES:READ</c>
/// specifically, never inferring it from <c>POLICIES:MANAGE</c>. Since the
/// Role/Permission/RolePermission catalog is global, not tenant-scoped, this
/// affected every tenant's ADMIN regardless of how it was provisioned — this
/// self-service signup path merely was the first thing to actually exercise
/// the real GET endpoint with a real, freshly-issued ADMIN token. Fixed by
/// migration <c>AddAdminReadPermissionsForPoliciesTemplatesSettings</c>, which
/// also grants <c>TEMPLATES:READ</c> (the same live gap, confirmed against
/// <c>TemplatesController</c>) and <c>SETTINGS:READ</c> (same documented
/// intent, no live consumer yet).
///
/// Reuses <see cref="ConversationMessageReceivedWorkflowRoundTripTests.Fixture"/>
/// purely for its real <c>Program.cs</c>-backed <c>ApiClient</c> against a
/// real, migrated Postgres — mirrors <see cref="AuthenticationRateLimitWorkflowRoundTripTests"/>'s
/// own precedent of reusing this fixture for a plain HTTP round trip that
/// needs neither the Worker subprocess nor RabbitMQ.
/// </summary>
public sealed class SelfServiceSignupAuthorizationParityTests : IClassFixture<ConversationMessageReceivedWorkflowRoundTripTests.Fixture>
{
    private readonly ConversationMessageReceivedWorkflowRoundTripTests.Fixture _fixture;

    public SelfServiceSignupAuthorizationParityTests(ConversationMessageReceivedWorkflowRoundTripTests.Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_ADMIN_created_by_self_service_signup_can_read_the_policy_catalog()
    {
        var signupRequest = new SignupRequest(
            CompanyName: $"Signup Parity Check {Guid.NewGuid():N}",
            AdminFullName: "Signup Parity Admin",
            AdminEmail: $"signup-parity-{Guid.NewGuid():N}@example.local",
            Password: "SignupParity!2026");

        var signupResponse = await _fixture.ApiClient.PostAsJsonAsync("/api/v1/signup", signupRequest);
        signupResponse.StatusCode.Should().Be(HttpStatusCode.OK, "self-service signup with valid, well-formed data must succeed");

        var signupResult = await signupResponse.Content.ReadFromJsonAsync<SignupResponse>();
        signupResult.Should().NotBeNull();

        using var policiesRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/policies");
        policiesRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signupResult!.Tokens.AccessToken);

        var policiesResponse = await _fixture.ApiClient.SendAsync(policiesRequest);

        policiesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "Documento 09 §15 lists Admin as full control (\"X\") over Políticas, so a freshly self-signed-up " +
            "ADMIN must be able to read the policy catalog, not just create new policy value versions");
    }
}
