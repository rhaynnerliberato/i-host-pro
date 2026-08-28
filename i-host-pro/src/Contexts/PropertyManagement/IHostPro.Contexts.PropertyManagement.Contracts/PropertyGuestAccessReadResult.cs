namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// The minimal, opaque result <see cref="IPropertyGuestAccessReader"/>
/// returns to Communication (Fase 10, Checkpoint 6.2 — Guest Access Secure
/// Delivery, synchronous exception #12) — never the
/// <c>PropertyAccessConfiguration</c> aggregate itself, never
/// <c>AccessCredentialSecretReference</c> (the reference is resolved
/// internally, in <c>PropertyManagement.Infrastructure</c>, never crossing
/// this boundary). <see cref="AccessCredential"/> exists only transiently, in
/// memory, for the duration of the call that produced it — never persisted
/// by the caller as-is (see <c>ADR-028</c>).
///
/// Either field may independently be <see langword="null"/> — a Property may
/// have instructions configured but no credential, or vice versa; the caller
/// decides per-field whether there is anything to deliver.
/// </summary>
public sealed record PropertyGuestAccessReadResult(
    string? AccessCredential,
    string? AccessInstructions);
