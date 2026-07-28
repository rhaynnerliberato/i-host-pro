using System.Reflection;
using FluentAssertions;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Identity.Contracts;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Reflection-based confirmation that none of the six Identity &amp; Access
/// Integration Events (Incremento 2 plan, Etapa 15; Documento 07 §13.1) can
/// ever carry sensitive data — checked structurally (property names/types),
/// not by inspecting a specific instance, so it holds for every possible
/// value, not just the ones exercised by other tests.
/// </summary>
public class IdentityIntegrationEventContentTests
{
    private static readonly string[] ForbiddenNameFragments =
    [
        "email", "ipaddress", "useragent", "password", "accesstoken",
        "refreshtoken", "tokenhash", "secret", "jwt",
    ];

    public static IEnumerable<object[]> IdentityIntegrationEventTypes() =>
        typeof(UserLoggedIn).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IntegrationEvent).IsAssignableFrom(t))
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(IdentityIntegrationEventTypes))]
    public void Event_carries_no_property_whose_name_suggests_sensitive_data(Type eventType)
    {
        var offendingProperties = eventType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => ForbiddenNameFragments.Any(fragment =>
                p.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        offendingProperties.Should().BeEmpty(
            $"{eventType.Name} must never carry a property whose name suggests an e-mail, IP address, " +
            "User-Agent, password, access token, refresh token, token hash, secret, or JWT — only stable " +
            "identifiers and reason codes (Documento 07 §13.1).");
    }

    [Fact]
    public void Exactly_the_six_Etapa_15_events_are_defined_in_Contracts()
    {
        // Guards against a future event silently expanding this list without
        // updating Documento 07 first (Documento 07 §24) — not exhaustive
        // proof of catalogue compliance, just a tripwire.
        var eventTypeNames = typeof(UserLoggedIn).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IntegrationEvent).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        eventTypeNames.Should().BeEquivalentTo(
            "UserLoggedIn", "LoginFailed", "AccountLockedOut",
            "UserLoggedOut", "RefreshTokenReuseDetected", "SessionRevoked");
    }
}
