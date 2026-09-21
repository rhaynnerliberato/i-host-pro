namespace IHostPro.BuildingBlocks.Infrastructure.Email;

/// <summary>
/// Non-secret SMTP transport config for the Development transactional-email
/// sender (local Mailpit — Self-Service Identity &amp; Onboarding Foundation
/// gate). Never used in a non-Development environment — see
/// <see cref="TransactionalEmailServiceCollectionExtensions"/>.
/// </summary>
public sealed class TransactionalEmailOptions
{
    public const string SectionName = "TransactionalEmail";

    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public string FromAddress { get; set; } = "no-reply@ihostpro.local";
    public string FromName { get; set; } = "iHostPro";
}
