using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.HomologScenarioProvisioning;

/// <summary>
/// CP5.3D-D corrective Decision Gate ("HomologSyntheticBusinessFixture"):
/// every value here is a fixed, clearly-labeled TEST/HOMOLOG/NON_CUSTOMER_DATA
/// identifier - never a real guest, never real PII, never anything that
/// could resolve to a real person. Purpose=REAL_RUNTIME_INTEGRATION_PROOF
/// only - this is a test fixture, never a commercial onboarding mechanism
/// (CommercialBusinessDataProvisioning=NOT_IMPLEMENTED).
/// </summary>
public static class HomologFixtureIdentifiers
{
    public const string PropertyCodeValue = "HOMOLOG-AI-TEST-01";
    public const string PropertyName = "HOMOLOG AI TEST PROPERTY - NON CUSTOMER DATA";
    public const string GuestName = "HOMOLOG AI TEST GUEST - NON CUSTOMER DATA";

    // Not a real E.164 number for any person - a synthetic, all-zero-after-
    // country-code digit string, used only as a stable lookup key for this
    // fixture's idempotency check. Never dialable, never used for any real
    // outbound send.
    public const string GuestPhone = "5500000000000";

    // Synthetic Meta phone_number_id - globally unique per
    // WhatsAppTenantRoute's own DB constraint, never a real Meta-issued id.
    public const string PhoneNumberId = "000000000000000";
}

public sealed record ScenarioResult(
    Guid PropertyId,
    bool PropertyCreated,
    Guid ReservationId,
    bool ReservationCreated,
    Guid WhatsAppTenantRouteId,
    bool RouteCreated);

/// <summary>
/// Idempotently reconciles the minimal real business-data chain needed to
/// exercise the REAL inbound-WhatsApp-webhook -> ConversationMessageReceived
/// -> AI Agent -> Anthropic pipeline: an active Property, a Confirmed
/// Reservation for it, and a WhatsAppTenantRoute mapping a synthetic
/// phone_number_id to the target tenant. Deliberately builds every
/// aggregate via its own real domain factory (Property.Create/.Activate,
/// Reservation.Create, WhatsAppTenantRoute.Create) and a plain
/// SaveChangesAsync - never through CreateReservationCommandHandler's full
/// Mediator/outbox pipeline, so ReservationCreated (and its many real
/// consumers, including an outbound WhatsApp send attempt) is never
/// enqueued. The actual inbound message that triggers
/// ConversationMessageReceived is sent separately, as a real signed HTTP
/// call to the real webhook endpoint - this class only prepares the data
/// that call needs to resolve against.
/// </summary>
public sealed class HomologScenarioProvisioner
{
    private readonly PropertyManagementDbContext _propertyDbContext;
    private readonly ReservationsDbContext _reservationsDbContext;
    private readonly ExternalIntegrationsDbContext _externalIntegrationsDbContext;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;

    public HomologScenarioProvisioner(
        PropertyManagementDbContext propertyDbContext,
        ReservationsDbContext reservationsDbContext,
        ExternalIntegrationsDbContext externalIntegrationsDbContext,
        ITenantContext tenantContext,
        TimeProvider timeProvider)
    {
        _propertyDbContext = propertyDbContext;
        _reservationsDbContext = reservationsDbContext;
        _externalIntegrationsDbContext = externalIntegrationsDbContext;
        _tenantContext = tenantContext;
        _timeProvider = timeProvider;
    }

    public async Task<ScenarioResult> ProvisionAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        _tenantContext.SetTenant(tenantId);

        var (propertyId, propertyCreated) = await EnsurePropertyAsync(tenantId, now, cancellationToken);
        var (reservationId, reservationCreated) = await EnsureReservationAsync(tenantId, propertyId, now, cancellationToken);
        var (routeId, routeCreated) = await EnsureWhatsAppRouteAsync(tenantId, now, cancellationToken);

        return new ScenarioResult(propertyId, propertyCreated, reservationId, reservationCreated, routeId, routeCreated);
    }

    private async Task<(Guid PropertyId, bool Created)> EnsurePropertyAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var propertyCode = PropertyCode.Create(HomologFixtureIdentifiers.PropertyCodeValue);

        // Property IS RLS-protected (unlike Tenant in the Identity tool this
        // mirrors) - the existence check itself must run AFTER SET LOCAL
        // app.tenant_id, or FORCE ROW LEVEL SECURITY hides every row
        // (including one already created by a prior run), making the "find"
        // half of find-or-create silently always miss and defeating
        // idempotency - caught by this tool's own integration test.
        await using var transaction = await _propertyDbContext.Database.BeginTransactionAsync(cancellationToken);
        await _propertyDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)", cancellationToken);

        var existing = await _propertyDbContext.Properties
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.NormalizedCode == propertyCode.NormalizedValue, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return (existing.Id, false);
        }

        // Fixed, non-real address - matches the real integration test
        // precedent's own placeholder shape (AnthropicRealAgentWorkflowRoundTripTests).
        var address = Address.Create("59090-000", "Rua Homolog Fixture", "0", null, "Ponta Negra", "Natal", "RN");
        var property = Property.Create(Guid.NewGuid(), tenantId, propertyCode, HomologFixtureIdentifiers.PropertyName, capacity: 4, condominiumId: null, address, now);
        property.Activate(now);

        _propertyDbContext.Properties.Add(property);
        await _propertyDbContext.SaveChangesAsync(cancellationToken);

        // Housekeeping's own property-eligibility projection - a real,
        // documented prerequisite for a Property to be reservation-eligible
        // (confirmed by the real AnthropicRealAgentWorkflowRoundTripTests
        // fixture, the only existing precedent for this exact chain).
        await _propertyDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO housekeeping.property_projection (tenant_id, property_id, is_active)
             VALUES ({tenantId}, {property.Id}, true)
             ON CONFLICT DO NOTHING
             """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return (property.Id, true);
    }

    private async Task<(Guid ReservationId, bool Created)> EnsureReservationAsync(Guid tenantId, Guid propertyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Same RLS-ordering requirement as EnsurePropertyAsync above -
        // Reservation is RLS-protected too, so the existence check must run
        // after SET LOCAL app.tenant_id.
        await using var transaction = await _reservationsDbContext.Database.BeginTransactionAsync(cancellationToken);
        await _reservationsDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)", cancellationToken);

        var existing = await _reservationsDbContext.Reservations
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.GuestPhone == HomologFixtureIdentifiers.GuestPhone, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return (existing.Id, false);
        }

        // ReservationByGuestPhoneReader requires exactly one Confirmed
        // reservation with this GuestPhone - a wide, safely-in-the-future
        // stay window keeps that true regardless of when this tool runs.
        var checkInAt = now.AddDays(1);
        var checkOutAt = now.AddDays(3);
        var reservation = Reservation.Create(
            Guid.NewGuid(), tenantId, propertyId, HomologFixtureIdentifiers.GuestName, HomologFixtureIdentifiers.GuestPhone,
            checkInAt, checkOutAt, guestCount: 1, now);

        _reservationsDbContext.Reservations.Add(reservation);
        await _reservationsDbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (reservation.Id, true);
    }

    private async Task<(Guid RouteId, bool Created)> EnsureWhatsAppRouteAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // WhatsAppTenantRoute is deliberately NOT tenant-owned/RLS-protected
        // (it exists specifically to resolve "which tenant" before any
        // TenantId is known) - no SET LOCAL app.tenant_id needed here.
        var existing = await _externalIntegrationsDbContext.WhatsAppTenantRoutes
            .SingleOrDefaultAsync(r => r.PhoneNumberId == HomologFixtureIdentifiers.PhoneNumberId, cancellationToken);
        if (existing is not null)
        {
            if (existing.TenantId != tenantId)
                throw new InvalidOperationException("The fixture's synthetic phone_number_id is already routed to a different tenant - refusing to reassign it.");
            return (existing.Id, false);
        }

        var route = WhatsAppTenantRoute.Create(Guid.NewGuid(), HomologFixtureIdentifiers.PhoneNumberId, tenantId, now);
        _externalIntegrationsDbContext.WhatsAppTenantRoutes.Add(route);
        await _externalIntegrationsDbContext.SaveChangesAsync(cancellationToken);

        return (route.Id, true);
    }
}
