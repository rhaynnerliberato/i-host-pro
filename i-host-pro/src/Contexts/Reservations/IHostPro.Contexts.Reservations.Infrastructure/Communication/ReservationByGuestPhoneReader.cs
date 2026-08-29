using System.Text.RegularExpressions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain.Enums;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Reservations.Infrastructure.Communication;

/// <inheritdoc cref="IReservationByGuestPhoneReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IReservationByGuestPhoneReader"/> (Fase 11, Checkpoint 1 —
/// ADR-029, synchronous exception #13) — lives in
/// <c>Reservations.Infrastructure</c>, the one layer allowed to touch
/// <see cref="ReservationsDbContext"/> directly. Mirrors
/// <c>ReservationScheduleReader</c>/<c>ReservationGuestContactReader</c>'s own
/// structural precedent exactly (ADR-024/ADR-019): its own short-lived,
/// read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/>, a throwaway local
/// <see cref="TenantContext"/>, no cache, no mutation.
///
/// No shared cross-context phone-normalization boundary exists in this
/// codebase today (audited before writing this — confirmed by a direct
/// search for any existing normalizer). Rather than invent a new
/// BuildingBlocks-tier utility unilaterally for a need proven in only two
/// contexts so far (the same bar ADR-021 already rejected once for
/// <c>IMessagingProvider</c>), this reader independently reduces the stored
/// <c>GuestPhone</c> to digits-only before comparing — the exact same rule
/// <c>ExternalIntegrations</c> applies when producing
/// <c>SenderPhoneNormalized</c> from Meta's <c>wa_id</c>. Both sides are
/// documented with the identical rule and each has its own unit test
/// asserting it; if a third context needs the same normalization, promoting
/// it to a shared utility becomes its own decision then, not assumed now.
/// </remarks>
public sealed class ReservationByGuestPhoneReader : IReservationByGuestPhoneReader
{
    private const string Purpose = "communication_inbound_guest_phone_resolution";
    private const string Caller = "Communication";

    private static readonly Regex NonDigits = new(@"\D+", RegexOptions.Compiled);

    private readonly ReservationsDbContext _dbContext;
    private readonly ILogger<ReservationByGuestPhoneReader> _logger;

    public ReservationByGuestPhoneReader(ReservationsDbContext dbContext, ILogger<ReservationByGuestPhoneReader> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ReservationCandidate>> FindEligibleByGuestPhoneAsync(
        Guid tenantId, string guestPhoneNormalized, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var normalizedInput = NonDigits.Replace(guestPhoneNormalized, string.Empty);

        // GuestPhone is free-form text (Fase 3, manual entry) — reduced to
        // digits-only client-side for comparison, same rule as the input.
        // Loaded once per tenant (RLS already scopes this to a single
        // tenant's row count, never platform-wide) rather than pushed into
        // SQL, since Npgsql has no built-in regexp_replace translation for
        // this shape and the per-tenant Confirmed-reservation volume is
        // small at this MVP stage.
        var confirmedWithPhone = await _dbContext.Reservations
            .AsNoTracking()
            .Where(r => r.Status == ReservationStatus.Confirmed)
            .Where(r => r.GuestPhone != null)
            .Select(r => new { r.Id, r.PropertyId, r.CheckInAt, r.CheckOutAt, r.GuestPhone })
            .ToListAsync(cancellationToken);

        var candidates = confirmedWithPhone
            .Where(r => NonDigits.Replace(r.GuestPhone!, string.Empty) == normalizedInput)
            .Select(r => new ReservationCandidate(r.Id, r.PropertyId, r.CheckInAt, r.CheckOutAt))
            .ToList();

        _logger.LogInformation(
            "Guest phone resolution for {Purpose} by {Caller}: tenant {TenantId} — {CandidateCount} candidate(s)",
            Purpose, Caller, tenantId, candidates.Count);

        return candidates;
    }
}
