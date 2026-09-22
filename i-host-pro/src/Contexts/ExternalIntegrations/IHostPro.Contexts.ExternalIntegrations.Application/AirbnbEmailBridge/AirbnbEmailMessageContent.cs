namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// One message's subject and body, fetched together by id via
/// <see cref="IAirbnbEmailMessageSource.GetMessageContentAsync"/> — both
/// fields the reservation-reminder parser requires (the parser's own
/// validation depends on the subject, not only the body). Either field may
/// still be <c>null</c> if Graph itself returns no value for it.
/// </summary>
public sealed record AirbnbEmailMessageContent(string? Subject, string? Body);
