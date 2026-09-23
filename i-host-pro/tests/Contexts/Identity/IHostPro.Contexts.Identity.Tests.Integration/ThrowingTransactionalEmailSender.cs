using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// Shared DI-graph stub for endpoint tests that never exercise forgot-password
/// but still need an <see cref="ITransactionalEmailSender"/> registered (the
/// real production sender, <c>ResendTransactionalEmailSender</c>, needs an
/// <c>IHttpClientFactory</c> registration these fixtures don't set up).
/// Replaces the deleted <c>UnconfiguredTransactionalEmailSender</c> for this
/// same purpose — fails loudly if a test path ever does call it.
/// </summary>
public sealed class ThrowingTransactionalEmailSender : ITransactionalEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "ThrowingTransactionalEmailSender was invoked - this test fixture was not expected to send an email.");
}
