namespace IHostPro.Contexts.GuestOperations.Api.Contracts;

/// <summary>
/// HTTP request body for <c>POST .../late-checkout</c> (Fase 10, Checkpoint
/// 3) — carries only the guest's requested new checkout time; tenant/actor
/// come exclusively from the caller's own token claims.
/// </summary>
public sealed record RequestLateCheckoutHttpRequest(DateTimeOffset RequestedCheckOutAt);
