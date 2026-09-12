using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Infrastructure;
using IHostPro.Contexts.Communication.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Communication.Tests.Unit.Infrastructure;

/// <summary>
/// Real Tenant WhatsApp Activation Readiness gate (SMALL_IMPLEMENTATION_GAP
/// plan), required DI test (§30): proves <c>AddCommunicationModule</c> selects
/// the correct <see cref="IOutboundMessageConnector"/> implementation per
/// environment — inspects the registered <see cref="ServiceDescriptor"/>
/// directly, never builds a provider, so no real Postgres/AWS/HTTP
/// dependency is needed (every downstream service this connector eventually
/// needs is only ever constructed lazily on actual resolution).
/// </summary>
public class CommunicationModuleExtensionsOutboundConnectorTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Fact]
    public void AddCommunicationModule_registers_FakeWhatsAppConnector_in_Development()
    {
        var services = new ServiceCollection();

        services.AddCommunicationModule(EmptyConfiguration, isDevelopmentEnvironment: true);

        var descriptor = services.Single(d => d.ServiceType == typeof(IOutboundMessageConnector));
        descriptor.ImplementationType.Should().Be(typeof(FakeWhatsAppConnector));
    }

    [Fact]
    public void AddCommunicationModule_registers_the_real_tenant_aware_connector_outside_Development()
    {
        var services = new ServiceCollection();

        services.AddCommunicationModule(EmptyConfiguration, isDevelopmentEnvironment: false);

        var descriptor = services.Single(d => d.ServiceType == typeof(IOutboundMessageConnector));
        descriptor.ImplementationType.Should().Be(typeof(ExternalIntegrationsWhatsAppConnector),
            "a properly enabled/configured tenant must reach the real Meta send path, never the old always-fail NotConfiguredOutboundMessageConnector");
    }
}
