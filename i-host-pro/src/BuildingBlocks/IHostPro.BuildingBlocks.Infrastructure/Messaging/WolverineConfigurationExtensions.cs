using Microsoft.Extensions.Configuration;
using Wolverine;
using Wolverine.RabbitMQ;

namespace IHostPro.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Shared RabbitMQ connection configuration for every IHostPro process that
/// registers Wolverine (IHostPro.Api and IHostPro.Worker). Extracted here to
/// avoid duplicating the same connection setup in every Host process
/// (Architecture Principles, Section 11 / ADR-004).
/// </summary>
public static class WolverineConfigurationExtensions
{
    public static void UseIHostProRabbitMq(this WolverineOptions opts, IConfiguration configuration, bool listen)
    {
        var transport = opts.UseRabbitMq(rabbit =>
        {
            rabbit.HostName = configuration["RabbitMq:Host"] ?? "localhost";
            rabbit.VirtualHost = configuration["RabbitMq:VirtualHost"] ?? "/";
            rabbit.UserName = configuration["RabbitMq:Username"] ?? "guest";
            rabbit.Password = configuration["RabbitMq:Password"] ?? "guest";
        });

        // IHostPro.Api only publishes Integration Events; it never consumes
        // messages — consumers/handlers live exclusively in IHostPro.Worker
        // (Architecture Principles, Section 2).
        if (!listen)
        {
            transport.UseSenderConnectionOnly();
        }
    }
}
