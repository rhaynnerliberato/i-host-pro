using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Email;

namespace IHostPro.BuildingBlocks.Tests.Unit;

public class UnconfiguredTransactionalEmailSenderTests
{
    [Fact]
    public async Task SendAsync_throws_because_no_production_provider_is_selected_yet()
    {
        var sender = new UnconfiguredTransactionalEmailSender();
        var message = new EmailMessage("guest@example.com", "Guest", "Subject", "Body");

        var act = () => sender.SendAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*production transactional email provider*");
    }
}
