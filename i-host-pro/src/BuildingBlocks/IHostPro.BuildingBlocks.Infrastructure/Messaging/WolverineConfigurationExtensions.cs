using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;

namespace IHostPro.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Shared RabbitMQ connection configuration for every IHostPro process that
/// registers Wolverine (IHostPro.Api and IHostPro.Worker). Extracted here to
/// avoid duplicating the same connection setup in every Host process
/// (Architecture Principles, Section 11 / ADR-004).
/// </summary>
public static class WolverineConfigurationExtensions
{
    /// <summary>
    /// Applies the shared host/vhost/user/password/TLS/port settings to a
    /// RabbitMQ.Client.ConnectionFactory from IConfiguration - the ONE place
    /// both Wolverine's own transport (via UseIHostProRabbitMq below) and any
    /// other RabbitMQ.Client.ConnectionFactory a process creates (e.g. a
    /// health check) must go through, so the two can never diverge on
    /// TLS/port again (CP5.3D-B2 corrective Decision Gate: the health check's
    /// own hand-rolled factory had never applied UseTls/Port, so it always
    /// attempted the client library's default plaintext port 5672 against
    /// Amazon MQ's TLS-only endpoint - deterministically Unhealthy in every
    /// environment that actually sets UseTls=true).
    /// </summary>
    public static void ApplyIHostProRabbitMqSettings(this ConnectionFactory factory, IConfiguration configuration)
    {
        factory.HostName = configuration["RabbitMq:Host"] ?? "localhost";
        factory.VirtualHost = configuration["RabbitMq:VirtualHost"] ?? "/";
        factory.UserName = configuration["RabbitMq:Username"] ?? "guest";
        factory.Password = configuration["RabbitMq:Password"] ?? "guest";

        var useTls = bool.TryParse(configuration["RabbitMq:UseTls"], out var useTlsFlag) && useTlsFlag;
        var configuredPort = int.TryParse(configuration["RabbitMq:Port"], out var parsedPort) ? parsedPort : (int?)null;

        // Fase 12, Checkpoint 5.1: Amazon MQ for RabbitMQ (the AWS pilot's
        // managed broker target) accepts TLS-only connections — there is
        // no plaintext AMQP endpoint. Local/dev brokers (docker-compose's
        // rabbitmq:3-management-alpine image) stay on the plaintext
        // default by omitting RabbitMq:UseTls entirely (defaults to
        // false, preserving every existing Testcontainers-based test's
        // "no port override" assumption); only Homologação/Production —
        // pointed at Amazon MQ — set RabbitMq:UseTls=true. Server
        // certificate validation is never bypassed; RabbitMq:Port lets an
        // environment override the port independently of UseTls when
        // needed, but is never required for either shape described here.
        if (useTls)
        {
            factory.Ssl.Enabled = true;
            factory.Ssl.ServerName = factory.HostName;
            factory.Port = configuredPort ?? 5671;
        }
        else if (configuredPort is { } explicitPort)
        {
            factory.Port = explicitPort;
        }
    }

    /// <summary>
    /// Returns the transport expression (widened from <c>void</c> for
    /// Checkpoint 6 homologação's messaging-topology provisioning fix) so
    /// <c>IHostPro.MigrationRunner</c> can chain <c>.DeclareExchange(...)</c>
    /// on the SAME connection configuration <c>IHostPro.Api</c>/<c>IHostPro.Worker</c>
    /// use — one source of truth for host/vhost/user/password/timeouts,
    /// never duplicated. Existing callers that used this as a statement are
    /// unaffected: discarding a non-void return value is legal C#.
    /// </summary>
    public static RabbitMqTransportExpression UseIHostProRabbitMq(this WolverineOptions opts, IConfiguration configuration, bool listen)
    {
        var clientTimeouts = configuration.GetSection(RabbitMqClientTimeoutOptions.SectionName)
            .Get<RabbitMqClientTimeoutOptions>() ?? new RabbitMqClientTimeoutOptions();
        RabbitMqClientTimeoutOptionsValidator.ValidateAndThrow(clientTimeouts);

        var transport = opts.UseRabbitMq(rabbit =>
        {
            rabbit.ApplyIHostProRabbitMqSettings(configuration);

            // Confirmed empirically (Incremento 2 plan, homologação real) that
            // RabbitMQ.Client's own defaults (30s / 20s) make an outbox
            // message's opportunistic delivery attempt block the originating
            // HTTP request for tens of seconds to minutes when the broker is
            // unreachable — the durable outbox itself already persists and
            // returns correctly; only these client-level timeouts needed
            // tuning so that attempt fails fast and falls back to the
            // background durability agent, per RabbitMqClientTimeoutOptions's
            // doc comment.
            rabbit.RequestedConnectionTimeout = clientTimeouts.ConnectTimeout;
            rabbit.ContinuationTimeout = clientTimeouts.ContinuationTimeout;
        });

        // IHostPro.Api only publishes Integration Events; it never consumes
        // messages — consumers/handlers live exclusively in IHostPro.Worker
        // (Architecture Principles, Section 2).
        if (!listen)
        {
            transport.UseSenderConnectionOnly();
        }

        return transport;
    }

    /// <summary>
    /// Registers a PostgreSQL-backed "ancillary" (per-Bounded-Context, not
    /// shared/global) durable message store enrolled to exactly one
    /// <c>DbContext</c> type (Incremento 2 plan, Etapa 15A; ADR-004; Architecture
    /// Principles, Section 8/11). "Ancillary" is Wolverine's own term for this —
    /// distinct from the (unused, today) "Main" store role — so each future
    /// Bounded Context gets its own store/schema, never a single shared one
    /// (approved decision: no global message store). The caller is responsible
    /// for also setting <c>opts.AutoBuildMessageStorageOnStartup = AutoCreate.None</c>
    /// — no host may create/alter these objects at runtime; only
    /// <c>IHostPro.MigrationRunner</c> may, via Wolverine/Weasel's own resource
    /// management (<c>host.SetupResources()</c>), never a plain EF Core migration.
    /// </summary>
    public static void EnrollAncillaryPostgresqlOutbox(
        this WolverineOptions opts, string connectionString, string schemaName, Type dbContextType) =>
        opts.PersistMessagesWithPostgresql(connectionString, schemaName, MessageStoreRole.Ancillary)
            .Enroll(dbContextType);
}
