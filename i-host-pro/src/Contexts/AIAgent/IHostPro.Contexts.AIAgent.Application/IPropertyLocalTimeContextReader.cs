namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Fase 11, Checkpoint 7 (mandate item 33-35, <c>TimezoneOwner=Property</c>).
/// Resolves the property behind a Reservation and its own configured IANA
/// time zone id (nullable — no backfill exists, mandate item 31), so the
/// Context Builder can inject a genuine current-local-time fact into the
/// prompt instead of ever letting the model assume the server's own
/// timezone. Implemented in AIAgent.Infrastructure exclusively, reusing the
/// exact two cross-context dispatcher calls <c>GetPropertyInformationTool</c>
/// already makes (Exceção 3) — no new synchronous exception.
/// </summary>
public interface IPropertyLocalTimeContextReader
{
    Task<PropertyLocalTimeContext?> GetByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken);
}

/// <summary><see cref="TimeZoneId"/> is <see langword="null"/> when the property has not been configured with one yet — the caller must never assume a timezone in that case.</summary>
public sealed record PropertyLocalTimeContext(Guid PropertyId, string? TimeZoneId);
