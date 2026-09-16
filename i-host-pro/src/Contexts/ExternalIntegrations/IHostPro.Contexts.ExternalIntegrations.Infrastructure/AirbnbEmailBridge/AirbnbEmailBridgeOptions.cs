namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Bound from <c>ExternalIntegrations:AirbnbEmailBridge</c>. No
/// <c>ClientSecret</c> setting exists by design — this is a public client
/// (Fase 9 review §1-2): the Microsoft Entra app registration must use the
/// "Mobile and desktop applications" platform with redirect URI
/// <c>http://localhost</c>, never a Web/confidential-client registration.
///
/// <see cref="ClientId"/> being unset is a legitimate, common state (no
/// developer has connected a mailbox yet) — resolving it happens lazily,
/// inside <see cref="MsalAirbnbEmailAuthenticator"/>, never validated at host
/// startup, so its absence never blocks Api/Worker startup for anyone not
/// using this feature (same lazy-fail-on-use philosophy as
/// <see cref="AesGcmTokenCacheProtector"/>'s own master key).
/// </summary>
public sealed class AirbnbEmailBridgeOptions
{
    public string? ClientId { get; set; }

    /// <summary>Must match the Entra app registration's "Mobile and desktop applications" redirect URI exactly.</summary>
    public string RedirectUri { get; set; } = "http://localhost";

    /// <summary>
    /// <c>common</c> supports both work/school accounts and personal
    /// Microsoft accounts (Outlook.com/Hotmail) in the same authority —
    /// required since the app registration's supported account type is
    /// "Any Entra ID tenant + personal Microsoft accounts".
    /// </summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com/common";

    /// <summary>
    /// Delegated Microsoft Graph scopes. <c>Mail.Read</c> only — deliberately
    /// not <c>Mail.ReadWrite</c>/<c>Mail.Send</c> (least privilege; nothing in
    /// this bridge ever writes to or sends from the connected mailbox).
    /// </summary>
    public string[] Scopes { get; set; } = ["Mail.Read"];

    /// <summary>
    /// Delta-polling gate — off by default (Fase 9 review §46): the Worker
    /// must never start reading a mailbox merely because the process starts.
    /// A tenant also needs a <c>Connected</c>+<c>IsEnabled</c> mailbox
    /// connection for polling to do anything even when this is true.
    /// </summary>
    public bool PollingEnabled { get; set; }

    public int PollingIntervalSeconds { get; set; } = 120;

    /// <summary>Graph well-known folder name — avoids a separate folder-id lookup call.</summary>
    public string MailFolderId { get; set; } = "inbox";

    /// <summary>
    /// Which tenants the Worker polls. Deliberately explicit configuration,
    /// not a cross-tenant directory table: <c>airbnb_email_mailbox_connections</c>
    /// is tenant-owned (RLS + Global Query Filter), so nothing can enumerate
    /// "every tenant with a connection" without first knowing the tenant —
    /// the same reason <c>WhatsAppTenantRoute</c> exists as a deliberate,
    /// separate global table for WhatsApp. Building an equivalent directory
    /// for a single local-dev mailbox would be speculative (ADR-022/023: no
    /// framework ahead of need); this list is the minimal alternative for a
    /// gate whose own success criteria only ever exercise one already-
    /// authorized local connection.
    /// </summary>
    public Guid[] PollingTenantIds { get; set; } = [];
}
