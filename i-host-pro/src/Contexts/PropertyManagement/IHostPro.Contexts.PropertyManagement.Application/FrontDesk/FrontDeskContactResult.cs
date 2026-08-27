namespace IHostPro.Contexts.PropertyManagement.Application.FrontDesk;

/// <summary>
/// The admin-facing read shape for a Condominium's configured front desk
/// contact (Fase 10, Checkpoint 4). Unlike <c>FrontDeskContactReadResult</c>
/// (Contracts, Communication-facing), this carries the full phone number —
/// the same tenant Administrator who configured it is reading their own
/// configuration back, not a cross-context PII read.
/// </summary>
public sealed record FrontDeskContactResult(
    Guid Id,
    Guid CondominiumId,
    string DisplayName,
    string PhoneNumber,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
