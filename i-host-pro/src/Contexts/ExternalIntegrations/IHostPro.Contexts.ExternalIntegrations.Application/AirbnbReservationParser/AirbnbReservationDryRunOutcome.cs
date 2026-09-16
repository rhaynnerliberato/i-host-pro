namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

/// <summary>
/// The result of evaluating one parsed reservation-reminder email as a
/// candidate import — DRY_RUN only. <see cref="WouldImport"/> is <c>true</c>
/// only when every required field parsed AND the listing title resolved to a
/// known <c>PropertyId</c>; this type is never passed to
/// <c>IAirbnbReservationSyncPublisher</c> or any other mutating call.
/// </summary>
public sealed record AirbnbReservationDryRunOutcome
{
    public bool WouldImport { get; }
    public bool ExternalReservationIdPresent { get; }
    public bool DatesParsed { get; }
    public bool GuestCountParsed { get; }
    public bool PropertyResolved { get; }
    public Guid? ResolvedPropertyId { get; }
    public AirbnbReservationReminderParseFailureReason? ParseFailureReason { get; }

    private AirbnbReservationDryRunOutcome(
        bool wouldImport, bool externalReservationIdPresent, bool datesParsed, bool guestCountParsed,
        bool propertyResolved, Guid? resolvedPropertyId, AirbnbReservationReminderParseFailureReason? parseFailureReason)
    {
        WouldImport = wouldImport;
        ExternalReservationIdPresent = externalReservationIdPresent;
        DatesParsed = datesParsed;
        GuestCountParsed = guestCountParsed;
        PropertyResolved = propertyResolved;
        ResolvedPropertyId = resolvedPropertyId;
        ParseFailureReason = parseFailureReason;
    }

    public static AirbnbReservationDryRunOutcome ParseFailed(AirbnbReservationReminderParseFailureReason reason) =>
        new(false, false, false, false, false, null, reason);

    public static AirbnbReservationDryRunOutcome PropertyNotResolved() =>
        new(false, true, true, true, false, null, null);

    public static AirbnbReservationDryRunOutcome Ready(Guid resolvedPropertyId) =>
        new(true, true, true, true, true, resolvedPropertyId, null);
}
