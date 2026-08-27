namespace IHostPro.Contexts.PropertyManagement.Application.Errors;

/// <summary>
/// Stable, framework-neutral codes for business-rule rejections raised by
/// Property Management commands/queries (Checkpoint 2 plan, item 8) — mirrors
/// <c>IdentityErrorCodes</c>'s own convention exactly: used as
/// <see cref="IHostPro.BuildingBlocks.Domain.Error.Code"/>, never paired with
/// a hardcoded message.
/// </summary>
public static class PropertyManagementErrorCodes
{
    /// <summary>
    /// An administrative lookup/update for a condominium id that does not
    /// exist, or that belongs to a different tenant — the two are
    /// indistinguishable by design (Checkpoint 2 plan, item 4).
    /// </summary>
    public const string CondominiumNotFound = "PropertyManagement.CondominiumNotFound";

    /// <summary>
    /// <c>PATCH /api/v1/condominiums/{id}</c> lost a genuine optimistic
    /// concurrency race on the <c>condominiums</c> row's <c>xmin</c> token to
    /// another concurrent update of the SAME condominium — translated from a
    /// caught <c>DbUpdateConcurrencyException</c>, never retried
    /// automatically (Checkpoint 2 plan, item 9).
    /// </summary>
    public const string CondominiumConcurrencyConflict = "PropertyManagement.CondominiumConcurrencyConflict";

    /// <summary>
    /// <c>PATCH /api/v1/condominiums/{id}</c> called with neither
    /// <c>name</c> nor <c>address</c> provided — rejected before any lookup,
    /// since there is nothing to change (Checkpoint 2 plan, item 7). Reused
    /// as-is by <c>UpdatePropertyCommand</c> when none of its five fields
    /// are supplied (Checkpoint 3 plan, item 5) — the rule is generic to
    /// Property Management PATCH endpoints, not Condominium-specific.
    /// </summary>
    public const string NoChangesProvided = "PropertyManagement.NoChangesProvided";

    /// <summary>
    /// An administrative lookup/update for a property id that does not
    /// exist, or that belongs to a different tenant — the two are
    /// indistinguishable by design (Checkpoint 3 plan, item 15).
    /// </summary>
    public const string PropertyNotFound = "PropertyManagement.PropertyNotFound";

    /// <summary>
    /// <c>POST /api/v1/properties</c> or <c>PATCH /api/v1/properties/{id}</c>
    /// tried to set a <c>code</c> whose normalized form already exists for
    /// another property of the same tenant — translated from the caught
    /// unique-constraint violation on <c>uq_properties_tenant_normalized_code</c>,
    /// never a generic <c>DbUpdateException</c> translation (Checkpoint 3
    /// plan, item 15/16).
    /// </summary>
    public const string PropertyCodeAlreadyExists = "PropertyManagement.PropertyCodeAlreadyExists";

    /// <summary>
    /// <c>PATCH /api/v1/properties/{id}</c> lost a genuine optimistic
    /// concurrency race on the <c>properties</c> row's <c>xmin</c> token to
    /// another concurrent update of the SAME property — translated from a
    /// caught <c>DbUpdateConcurrencyException</c>, never retried
    /// automatically (Checkpoint 3 plan, item 15/18).
    /// </summary>
    public const string PropertyConcurrencyConflict = "PropertyManagement.PropertyConcurrencyConflict";

    /// <summary>
    /// A property's final combined state (after conceptually applying every
    /// supplied field, for Create or Update alike) would have neither its
    /// own address nor a linked condominium to resolve one from (Checkpoint
    /// 3 plan, item 3/4/15). Also used by <c>ActivatePropertyCommand</c>
    /// (Checkpoint 4 plan, item 5) — activation requires the same effective
    /// address invariant Create/Update already enforce.
    /// </summary>
    public const string PropertyAddressRequired = "PropertyManagement.PropertyAddressRequired";

    /// <summary>
    /// <c>POST /api/v1/properties/{id}/activate</c> called on a property
    /// already <c>Active</c> — the transition is a no-op the endpoint
    /// deliberately rejects rather than silently accepts (Checkpoint 4 plan,
    /// item 4).
    /// </summary>
    public const string PropertyAlreadyActive = "PropertyManagement.PropertyAlreadyActive";

    /// <summary>
    /// <c>POST /api/v1/properties/{id}/deactivate</c> called on a property
    /// already <c>Inactive</c> (Checkpoint 4 plan, item 4).
    /// </summary>
    public const string PropertyAlreadyInactive = "PropertyManagement.PropertyAlreadyInactive";

    /// <summary>
    /// Any lifecycle action called on a property already <c>Archived</c> —
    /// <c>Archived</c> is terminal in this increment, no restoration exists
    /// (Checkpoint 4 plan, item 3/4).
    /// </summary>
    public const string PropertyAlreadyArchived = "PropertyManagement.PropertyAlreadyArchived";

    /// <summary>
    /// A lifecycle transition that is structurally impossible from the
    /// property's current status, distinct from "already in the target
    /// status" — e.g. <c>Deactivate</c> on a <c>Draft</c> property, or
    /// <c>Archive</c> on an <c>Active</c> one (Checkpoint 4 plan, item 3/4).
    /// </summary>
    public const string InvalidPropertyStatusTransition = "PropertyManagement.InvalidPropertyStatusTransition";

    /// <summary>
    /// <c>PATCH /api/v1/properties/{id}</c> called on a property already
    /// <c>Archived</c> — rejected before any diffing or no-op evaluation,
    /// even when every supplied field already matches the current value
    /// (Checkpoint 4 plan, item 6).
    /// </summary>
    public const string ArchivedPropertyCannotBeModified = "PropertyManagement.ArchivedPropertyCannotBeModified";

    /// <summary>
    /// <c>POST /api/v1/properties/{id}/owners</c> referenced an
    /// <c>ownerUserId</c> that does not exist within the caller's tenant, or
    /// belongs to a different tenant — indistinguishable by design, same
    /// convention as every other cross-tenant lookup (Checkpoint 5 plan, item
    /// 7). Distinct from <see cref="PropertyNotFound"/>, which is about the
    /// Property side of the link.
    /// </summary>
    public const string OwnerUserNotFound = "PropertyManagement.OwnerUserNotFound";

    /// <summary>
    /// <c>POST /api/v1/properties/{id}/owners</c> referenced a user that
    /// exists but is currently blocked, or does not currently hold
    /// <c>PROPERTY_OWNER</c> — translated from
    /// <c>IIdentityUserEligibilityReader</c>'s result (Checkpoint 5 plan,
    /// item 7).
    /// </summary>
    public const string OwnerUserNotEligible = "PropertyManagement.OwnerUserNotEligible";

    /// <summary>
    /// <c>POST /api/v1/properties/{id}/owners</c> referenced an
    /// (propertyId, ownerUserId) pair that is already linked — translated
    /// from the caught unique-constraint violation on
    /// <c>uq_property_owners_tenant_property_owner</c>, never a generic
    /// <c>DbUpdateException</c> translation, and also checked structurally
    /// before the insert is attempted (Checkpoint 5 plan, item 7/12).
    /// </summary>
    public const string PropertyOwnerAlreadyLinked = "PropertyManagement.PropertyOwnerAlreadyLinked";

    /// <summary>
    /// <c>DELETE /api/v1/properties/{id}/owners/{ownerUserId}</c> referenced
    /// a link that does not exist — including a repeated removal, and a
    /// removal that lost a genuine concurrency race to another concurrent
    /// removal of the SAME link (translated from a caught
    /// <c>DbUpdateConcurrencyException</c>, never <see cref="PropertyConcurrencyConflict"/>
    /// — <c>property_owners</c> carries no version token of its own, and the
    /// approved semantics for a concurrent double-unlink is "the loser sees
    /// not-found", not "conflict" — Checkpoint 5 plan, item 8/13).
    /// </summary>
    public const string PropertyOwnerNotLinked = "PropertyManagement.PropertyOwnerNotLinked";

    /// <summary>
    /// <c>GET /api/v1/condominiums/{id}/front-desk-contact</c> called for a
    /// condominium that has no <c>FrontDeskContact</c> configured yet — a
    /// distinct case from <see cref="CondominiumNotFound"/> (Fase 10,
    /// Checkpoint 4).
    /// </summary>
    public const string FrontDeskContactNotFound = "PropertyManagement.FrontDeskContactNotFound";
}
