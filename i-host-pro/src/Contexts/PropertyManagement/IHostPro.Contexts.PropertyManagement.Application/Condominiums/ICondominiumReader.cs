using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// The two read-only administrative condominium queries (Checkpoint 2 plan,
/// item 4) — mirrors <c>IUserAdministrationReader</c>'s shape.
/// </summary>
public interface ICondominiumReader
{
    /// <summary>
    /// <paramref name="pageSize"/> is defensively clamped to the fixed
    /// maximum (100) by the implementation itself — never trusts the caller
    /// alone (Checkpoint 2 plan, item 4: "nunca executar consulta sem
    /// limite"). Never fetches <c>Address</c> — <see cref="CondominiumSummaryResult"/>
    /// does not carry it (Checkpoint 2 plan, item 4: "não retornar endereço
    /// completo").
    /// </summary>
    Task<PagedResult<CondominiumSummaryResult>> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<CondominiumResult?> GetByIdAsync(Guid condominiumId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a condominium's address by id, tenant-scoped — used by
    /// Property Management's own Property Create/Update/Detail flows both to
    /// verify a Property's <c>CondominiumId</c> exists within the caller's
    /// tenant and, when it does, to resolve the Property's effective address
    /// (Checkpoint 3 plan, item 9/12/15). Returns <c>null</c> for both "does
    /// not exist" and "belongs to a different tenant" — the two are
    /// indistinguishable by design, mirroring <see cref="GetByIdAsync"/>.
    /// </summary>
    Task<AddressResult?> GetAddressByIdAsync(Guid condominiumId, CancellationToken cancellationToken);
}
