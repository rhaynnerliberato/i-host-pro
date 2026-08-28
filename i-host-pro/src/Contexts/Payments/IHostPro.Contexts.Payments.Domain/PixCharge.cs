using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Payments.Domain.Enums;

namespace IHostPro.Contexts.Payments.Domain;

/// <summary>
/// A single PIX charge created against a <c>LateCheckoutRequest</c> pending
/// payment (Fase 10, Checkpoint 5 — PIX/Payment Deterministic Foundation).
/// Owns exclusively the financial lifecycle of the charge itself —
/// <c>LateCheckoutRequest</c> (Guest Operations) owns the payment BOUNDARY
/// (<c>PendingPayment</c> → <c>Approved</c>/<c>Denied</c>), never the
/// charge's own state machine.
///
/// <see cref="Amount"/>/<see cref="CurrencyCode"/> are a snapshot taken once
/// at <see cref="Create"/> from the triggering
/// <c>LateCheckoutPaymentRequired</c> event — Payments never re-reads or
/// recalculates <c>LateCheckoutPolicy</c>/percentage pricing (mandate item
/// 7/8). <see cref="CurrencyCode"/> is deliberately BRL-only this checkpoint
/// (mandate item 6) — <see cref="Create"/> rejects anything else.
///
/// <see cref="QrCodePayload"/> is sensitive OPERATIONAL payment data — not a
/// credential/API key (never routed through the platform's
/// <c>*SecretReference</c> convention) — persisted here as an ordinary
/// column, protected the same way every other tenant-owned column is (RLS +
/// tenant isolation), never logged, never placed in an Integration Event,
/// never in a query string (explicit product decision — see ADR-025).
/// </summary>
public sealed class PixCharge : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid LateCheckoutRequestId { get; private set; }
    public Guid ReservationId { get; private set; }
    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = null!;
    public PixChargeStatus Status { get; private set; }
    public string? ProviderChargeId { get; private set; }
    public string? QrCodePayload { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public DateTimeOffset? ConfirmedAtUtc { get; private set; }
    public DateTimeOffset? FailedAtUtc { get; private set; }
    public DateTimeOffset? ExpiredAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private PixCharge()
    {
        // EF Core materialization.
    }

    private PixCharge(
        Guid id, Guid tenantId, Guid lateCheckoutRequestId, Guid reservationId,
        decimal amount, string currencyCode, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        LateCheckoutRequestId = lateCheckoutRequestId;
        ReservationId = reservationId;
        Amount = amount;
        CurrencyCode = currencyCode;
        Status = PixChargeStatus.Pending;
        IdempotencyKey = Guid.NewGuid();
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// Creates a new charge in <see cref="PixChargeStatus.Pending"/>, with no
    /// provider data yet (<see cref="ProviderChargeId"/>/<see cref="QrCodePayload"/>
    /// remain <see langword="null"/> until <see cref="RecordProviderAcceptance"/>).
    /// </summary>
    public static PixCharge Create(
        Guid id, Guid tenantId, Guid lateCheckoutRequestId, Guid reservationId,
        decimal amount, string currencyCode, DateTimeOffset now)
    {
        if (lateCheckoutRequestId == Guid.Empty)
            throw new ArgumentException("Late checkout request id cannot be empty.", nameof(lateCheckoutRequestId));

        if (reservationId == Guid.Empty)
            throw new ArgumentException("Reservation id cannot be empty.", nameof(reservationId));

        if (amount <= 0)
            throw new ArgumentException("Amount must be positive.", nameof(amount));

        if (!string.Equals(currencyCode, "BRL", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only BRL is supported in this checkpoint (Fase 10, Checkpoint 5 mandate item 6).",
                nameof(currencyCode));
        }

        return new PixCharge(id, tenantId, lateCheckoutRequestId, reservationId, amount, currencyCode, now);
    }

    /// <summary>
    /// Records the (fake, this checkpoint) provider's acceptance of the
    /// charge creation — never a state transition, <see cref="Status"/>
    /// stays <see cref="PixChargeStatus.Pending"/>: the charge now has real
    /// provider data and is awaiting confirmation.
    /// </summary>
    public void RecordProviderAcceptance(string providerChargeId, string qrCodePayload, DateTimeOffset? expiresAtUtc, DateTimeOffset now)
    {
        if (Status != PixChargeStatus.Pending)
            throw new InvalidOperationException($"Cannot record provider acceptance for a charge in status '{Status}'.");

        ProviderChargeId = providerChargeId;
        QrCodePayload = qrCodePayload;
        ExpiresAtUtc = expiresAtUtc;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// <see cref="PixChargeStatus.Pending"/> → <see cref="PixChargeStatus.Failed"/>.
    /// Idempotent no-op when already <see cref="PixChargeStatus.Confirmed"/>,
    /// <see cref="PixChargeStatus.Failed"/>, or <see cref="PixChargeStatus.Expired"/>
    /// — a real confirmation (or an already-settled terminal state) always
    /// takes precedence over a late/duplicate failure signal (mandate item 10).
    /// </summary>
    public void Fail(DateTimeOffset now)
    {
        if (Status is PixChargeStatus.Confirmed or PixChargeStatus.Failed or PixChargeStatus.Expired)
            return;

        Status = PixChargeStatus.Failed;
        FailedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// <see cref="PixChargeStatus.Pending"/> → <see cref="PixChargeStatus.Expired"/>
    /// (Fase 10, Checkpoint 5.1 — Payment Failure/Expiration Evidence
    /// Corrective Gate, mandate item 6). Mirrors <see cref="Fail"/>'s own
    /// idempotent-no-op guard exactly: a real confirmation or an
    /// already-settled terminal state (including an already-<see cref="PixChargeStatus.Failed"/>
    /// charge — no approved transition between the two negative terminal
    /// states exists this checkpoint) always takes precedence over a late or
    /// out-of-order expiration signal.
    /// </summary>
    public void Expire(DateTimeOffset now)
    {
        if (Status is PixChargeStatus.Confirmed or PixChargeStatus.Failed or PixChargeStatus.Expired)
            return;

        Status = PixChargeStatus.Expired;
        ExpiredAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// Applies confirmation per the exact matrix approved for this
    /// checkpoint (mandate item 10): <see cref="PixChargeStatus.Pending"/>,
    /// <see cref="PixChargeStatus.Failed"/>, and <see cref="PixChargeStatus.Expired"/>
    /// all move forward to <see cref="PixChargeStatus.Confirmed"/> — a real
    /// confirmation of money received takes precedence over any negative or
    /// out-of-order prior status. Already-<see cref="PixChargeStatus.Confirmed"/>
    /// is an idempotent no-op (duplicate confirmation delivery).
    /// <see cref="PixChargeStatus.Cancelled"/> → Confirmed is NOT an approved
    /// transition — it throws rather than silently deciding either way,
    /// exactly as the mandate requires ("PARE e reporte" if this scenario
    /// ever arises); nothing in this checkpoint's own code paths ever sets
    /// <see cref="PixChargeStatus.Cancelled"/>, so this branch is
    /// unreachable today but left as an explicit guard, never silently
    /// allowed or silently ignored.
    /// </summary>
    public void Confirm(DateTimeOffset now)
    {
        switch (Status)
        {
            case PixChargeStatus.Confirmed:
                return;
            case PixChargeStatus.Pending:
            case PixChargeStatus.Failed:
            case PixChargeStatus.Expired:
                Status = PixChargeStatus.Confirmed;
                ConfirmedAtUtc = now;
                UpdatedAtUtc = now;
                return;
            case PixChargeStatus.Cancelled:
                throw new PixChargeCancelledConfirmationConflictException(Id);
            default:
                throw new ArgumentOutOfRangeException(nameof(Status), Status, "Unmapped PixChargeStatus.");
        }
    }
}
