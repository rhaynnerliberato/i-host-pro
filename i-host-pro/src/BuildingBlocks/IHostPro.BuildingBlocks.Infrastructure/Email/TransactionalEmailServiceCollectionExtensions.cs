using IHostPro.BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.BuildingBlocks.Infrastructure.Email;

/// <summary>
/// Registers <see cref="ITransactionalEmailSender"/> (Self-Service Identity
/// &amp; Onboarding Foundation gate). Mirrors <c>AddIdentityModule</c>'s own
/// <c>isDevelopmentEnvironment</c> parameter convention: the real Mailpit
/// SMTP transport is registered only in Development; every other
/// environment gets <see cref="UnconfiguredTransactionalEmailSender"/>,
/// since no real production provider has been chosen yet.
/// </summary>
public static class TransactionalEmailServiceCollectionExtensions
{
    public static IServiceCollection AddIHostProTransactionalEmail(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopmentEnvironment)
    {
        services.Configure<TransactionalEmailOptions>(configuration.GetSection(TransactionalEmailOptions.SectionName));

        if (isDevelopmentEnvironment)
            services.AddScoped<ITransactionalEmailSender, MailpitTransactionalEmailSender>();
        else
            services.AddScoped<ITransactionalEmailSender, UnconfiguredTransactionalEmailSender>();

        return services;
    }
}
