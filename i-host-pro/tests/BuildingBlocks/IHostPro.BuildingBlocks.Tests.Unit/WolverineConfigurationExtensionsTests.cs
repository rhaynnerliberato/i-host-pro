using System.Linq;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using Wolverine;

namespace IHostPro.BuildingBlocks.Tests.Unit;

/// <summary>
/// Fase 12, Checkpoint 5.1 — proves <see cref="WolverineConfigurationExtensions.UseIHostProRabbitMq"/>'s
/// TLS wiring deterministically, without ever connecting to a broker (real or
/// Amazon MQ). Inspects the configured <see cref="ConnectionFactory"/> via
/// reflection because <c>RabbitMqTransportExpression.Transport</c> is not
/// accessible outside Wolverine's own assembly, while its
/// <c>ConnectionFactory</c> property is public — confirmed empirically before
/// writing these tests.
/// </summary>
public class WolverineConfigurationExtensionsTests
{
    private static ConnectionFactory ResolveConnectionFactory(IConfiguration configuration, bool listen = false)
    {
        var opts = new WolverineOptions();
        opts.UseIHostProRabbitMq(configuration, listen);

        var rabbitMqTransport = opts.Transports.Single(t => t.GetType().Name == "RabbitMqTransport");
        return (ConnectionFactory)rabbitMqTransport.GetType()
            .GetProperty("ConnectionFactory")!.GetValue(rabbitMqTransport)!;
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void UseTls_absent_preserves_the_existing_local_plaintext_behaviour()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = "guest",
            ["RabbitMq:Password"] = "guest",
        });

        var connectionFactory = ResolveConnectionFactory(configuration);
        var untouchedDefault = new ConnectionFactory();

        connectionFactory.Ssl.Enabled.Should().BeFalse();
        connectionFactory.Port.Should().Be(untouchedDefault.Port, "no environment sets RabbitMq:UseTls/RabbitMq:Port today, so the port must never be touched");
    }

    [Fact]
    public void UseTls_false_explicitly_also_preserves_plaintext_behaviour()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:UseTls"] = "false",
        });

        var connectionFactory = ResolveConnectionFactory(configuration);

        connectionFactory.Ssl.Enabled.Should().BeFalse();
    }

    [Fact]
    public void UseTls_true_enables_ssl_and_defaults_to_the_amqps_port()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "b-example.mq.sa-east-1.amazonaws.com",
            ["RabbitMq:UseTls"] = "true",
        });

        var connectionFactory = ResolveConnectionFactory(configuration);

        connectionFactory.Ssl.Enabled.Should().BeTrue();
        connectionFactory.Ssl.ServerName.Should().Be("b-example.mq.sa-east-1.amazonaws.com");
        connectionFactory.Port.Should().Be(5671);
    }

    [Fact]
    public void UseTls_true_respects_an_explicit_port_override_instead_of_the_5671_default()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "b-example.mq.sa-east-1.amazonaws.com",
            ["RabbitMq:UseTls"] = "true",
            ["RabbitMq:Port"] = "5672",
        });

        var connectionFactory = ResolveConnectionFactory(configuration);

        connectionFactory.Ssl.Enabled.Should().BeTrue();
        connectionFactory.Port.Should().Be(5672);
    }

    [Fact]
    public void UseTls_false_still_respects_an_explicit_port_override()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:UseTls"] = "false",
            ["RabbitMq:Port"] = "5673",
        });

        var connectionFactory = ResolveConnectionFactory(configuration);

        connectionFactory.Ssl.Enabled.Should().BeFalse();
        connectionFactory.Port.Should().Be(5673);
    }

    [Fact]
    public void UseTls_true_never_relaxes_server_certificate_validation()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "b-example.mq.sa-east-1.amazonaws.com",
            ["RabbitMq:UseTls"] = "true",
        });

        var connectionFactory = ResolveConnectionFactory(configuration);
        var untouchedSslOption = new ConnectionFactory().Ssl;

        // Regression guard against a future edit accidentally bypassing
        // certificate validation (e.g. AcceptablePolicyErrors set to accept
        // any policy error, or a permissive CertificateValidationCallback) —
        // this must always equal the library's own strict default.
        connectionFactory.Ssl.AcceptablePolicyErrors.Should().Be(untouchedSslOption.AcceptablePolicyErrors);
        connectionFactory.Ssl.CertificateValidationCallback.Should().BeNull();
    }

    [Fact]
    public void UseTls_true_preserves_virtual_host_and_credentials_from_configuration()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "b-example.mq.sa-east-1.amazonaws.com",
            ["RabbitMq:VirtualHost"] = "ihostpro-homolog",
            ["RabbitMq:Username"] = "ihostpro-app",
            ["RabbitMq:Password"] = "some-secret",
            ["RabbitMq:UseTls"] = "true",
        });

        var connectionFactory = ResolveConnectionFactory(configuration);

        connectionFactory.VirtualHost.Should().Be("ihostpro-homolog");
        connectionFactory.UserName.Should().Be("ihostpro-app");
        connectionFactory.Password.Should().Be("some-secret");
    }

    /// <summary>
    /// CP5.3D-B2 corrective Decision Gate: <see cref="WolverineConfigurationExtensions.ApplyIHostProRabbitMqSettings"/>
    /// is the exact method both <see cref="WolverineConfigurationExtensions.UseIHostProRabbitMq"/>
    /// (tested above via Wolverine's own transport) and each Host process's
    /// standalone RabbitMQ health check must call - a real, live bug slipped
    /// through undetected because the health check built its own
    /// <see cref="ConnectionFactory"/> by hand and never applied UseTls/Port,
    /// so it always attempted the client library's default plaintext port
    /// 5672 against Amazon MQ's TLS-only endpoint. These tests exercise the
    /// shared method directly - a plain ConnectionFactory, no Wolverine
    /// reflection needed - so either call site regressing to a hand-rolled,
    /// divergent factory is caught immediately.
    /// </summary>
    public class ApplyIHostProRabbitMqSettingsTests
    {
        private static IConfiguration BuildConfiguration(IDictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        [Fact]
        public void UseTls_true_enables_ssl_and_defaults_to_the_amqps_port()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "b-example.mq.sa-east-1.amazonaws.com",
                ["RabbitMq:UseTls"] = "true",
            });

            var factory = new ConnectionFactory();
            factory.ApplyIHostProRabbitMqSettings(configuration);

            factory.Ssl.Enabled.Should().BeTrue();
            factory.Ssl.ServerName.Should().Be("b-example.mq.sa-east-1.amazonaws.com");
            factory.Port.Should().Be(5671);
        }

        [Fact]
        public void UseTls_absent_preserves_the_existing_local_plaintext_behaviour()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "localhost",
            });

            var factory = new ConnectionFactory();
            var untouchedDefault = new ConnectionFactory();
            factory.ApplyIHostProRabbitMqSettings(configuration);

            factory.Ssl.Enabled.Should().BeFalse();
            factory.Port.Should().Be(untouchedDefault.Port, "no environment sets RabbitMq:UseTls/RabbitMq:Port today, so the port must never be touched");
        }

        [Fact]
        public void UseTls_true_preserves_virtual_host_and_credentials_from_configuration()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["RabbitMq:Host"] = "b-example.mq.sa-east-1.amazonaws.com",
                ["RabbitMq:VirtualHost"] = "ihostpro-homolog",
                ["RabbitMq:Username"] = "ihostpro-app",
                ["RabbitMq:Password"] = "some-secret",
                ["RabbitMq:UseTls"] = "true",
            });

            var factory = new ConnectionFactory();
            factory.ApplyIHostProRabbitMqSettings(configuration);

            factory.VirtualHost.Should().Be("ihostpro-homolog");
            factory.UserName.Should().Be("ihostpro-app");
            factory.Password.Should().Be("some-secret");
        }
    }
}
