namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Minimal, provider-neutral transactional email payload (Self-Service
/// Identity &amp; Onboarding Foundation gate). Deliberately narrow — no
/// attachments, no template engine, no marketing/notification-center
/// concerns: the only sender consuming this today is the password-reset
/// flow, and this shape must not grow beyond what a real caller needs.
/// </summary>
public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string PlainTextBody);
