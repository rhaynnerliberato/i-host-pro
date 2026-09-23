using IHostPro.BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.BuildingBlocks.Infrastructure.Email;

/// <summary>
/// Registers <see cref="ITransactionalEmailSender"/> (Self-Service Identity
/// &amp; Onboarding Foundation gate; provider selected by the Production
/// Transactional Email Provider gate). Mirrors <c>AddIdentityModule</c>'s own
/// <c>isDevelopmentEnvironment</c> parameter convention: the real Mailpit
/// SMTP transport is registered only in Development; every other environment
/// gets <see cref="ResendTransactionalEmailSender"/>, which itself fails
/// loudly on first use if unconfigured — never a silent fallback to Mailpit
/// outside Development.
/// </summary>
public static class TransactionalEmailServiceCollectionExtensions
{
    public static IServiceCollection AddIHostProTransactionalEmail(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopmentEnvironment)
    {
        services.Configure<TransactionalEmailOptions>(configuration.GetSection(TransactionalEmailOptions.SectionName));
        services.Configure<ResendOptions>(configuration.GetSection(ResendOptions.SectionName));

        if (isDevelopmentEnvironment)
        {
            services.AddScoped<ITransactionalEmailSender, MailpitTransactionalEmailSender>();
        }
        else
        {
            services.AddHttpClient(ResendTransactionalEmailSender.HttpClientName);
            services.AddScoped<ITransactionalEmailSender, ResendTransactionalEmailSender>();
        }

        return services;
    }
}
