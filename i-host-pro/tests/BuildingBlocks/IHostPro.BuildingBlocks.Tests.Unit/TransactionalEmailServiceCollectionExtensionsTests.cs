using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.BuildingBlocks.Tests.Unit;

public class TransactionalEmailServiceCollectionExtensionsTests
{
    [Fact]
    public void AddIHostProTransactionalEmail_registers_the_Mailpit_sender_in_Development()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();

        services.AddIHostProTransactionalEmail(configuration, isDevelopmentEnvironment: true);
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITransactionalEmailSender>().Should().BeOfType<MailpitTransactionalEmailSender>();
    }

    [Fact]
    public void AddIHostProTransactionalEmail_registers_the_fail_loud_stub_outside_Development()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();

        services.AddIHostProTransactionalEmail(configuration, isDevelopmentEnvironment: false);
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITransactionalEmailSender>().Should().BeOfType<UnconfiguredTransactionalEmailSender>();
    }
}
