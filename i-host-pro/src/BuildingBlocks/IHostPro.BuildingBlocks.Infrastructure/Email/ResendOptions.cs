namespace IHostPro.BuildingBlocks.Infrastructure.Email;

/// <summary>
/// Config for the production <see cref="ResendTransactionalEmailSender"/>
/// (Production Transactional Email Provider gate). <see cref="ApiKey"/> is
/// secret — resolved from environment/user-secrets configuration only, never
/// hardcoded, logged, or committed. Deliberately separate from
/// <see cref="TransactionalEmailOptions"/> (that class is documented as
/// Development/Mailpit-only) rather than reused/expanded.
/// </summary>
public sealed class ResendOptions
{
    public const string SectionName = "Resend";

    public string ApiKey { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
}
