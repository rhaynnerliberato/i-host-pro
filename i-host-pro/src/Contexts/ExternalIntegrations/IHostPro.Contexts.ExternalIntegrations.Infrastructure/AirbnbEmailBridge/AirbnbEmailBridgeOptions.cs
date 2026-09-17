namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Bound from <c>ExternalIntegrations:AirbnbEmailBridge</c>.
///
/// <see cref="ClientId"/> being unset is a legitimate, common state (no
/// developer has connected a mailbox yet) — resolving it happens lazily,
/// inside <see cref="MsalAirbnbEmailAuthenticator"/>, never validated at host
/// startup, so its absence never blocks Api/Worker startup for anyone not
/// using this feature (same lazy-fail-on-use philosophy as
/// <see cref="AesGcmTokenCacheProtector"/>'s own master key).
///
/// Web OAuth architecture gate: the SAME Entra app registration now also
/// carries a Web platform redirect URI + a Client Secret credential
/// (<see cref="ClientSecret"/>/<see cref="WebRedirectUri"/>), ALONGSIDE the
/// original "Mobile and desktop applications" platform (<see cref="RedirectUri"/>)
/// — the local interactive public-client flow is preserved unchanged, never
/// removed. <see cref="ClientSecret"/> is resolved the same lazy, never-
/// validated-at-startup way as <see cref="ClientId"/> — via .NET User Secrets
/// or an environment variable, never committed.
/// </summary>
public sealed class AirbnbEmailBridgeOptions
{
    public string? ClientId { get; set; }

    /// <summary>Must match the Entra app registration's "Mobile and desktop applications" redirect URI exactly.</summary>
    public string RedirectUri { get; set; } = "http://localhost";

    /// <summary>
    /// The Web OAuth confidential-client credential. Never committed, never
    /// logged, never returned through any endpoint (Web OAuth architecture
    /// gate, item 34). Absent by default: everywhere the confidential-client
    /// web flow has not been set up yet (including every environment before
    /// the Entra human handoff step), the legacy local public-client flow
    /// keeps working exactly as before — see <see cref="MsalAirbnbEmailAuthenticator"/>'s
    /// silent-auth client-selection remarks.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The Web OAuth callback URL registered as a "Web" platform redirect URI
    /// on the same Entra app registration — e.g.
    /// <c>http://localhost:5140/api/v1/integrations/airbnb-email/oauth/callback</c>
    /// locally. Environment-specific, never a secret — deliberately separate
    /// from <see cref="RedirectUri"/> (the legacy loopback redirect, kept
    /// unchanged for the local interactive flow).
    /// </summary>
    public string? WebRedirectUri { get; set; }

    /// <summary>
    /// Where the Api's own <c>oauth/callback</c> 302-redirects the browser
    /// back to after completing (or failing) the exchange — e.g.
    /// <c>http://localhost:4200/integrations/airbnb-email</c> locally,
    /// <c>https://app.homolog.ihostpro.com.br/integrations/airbnb-email</c>
    /// in homolog. A gap discovered only during implementation of the
    /// architecture gate's approved design, not called out by name in the
    /// original report: the Api and the Angular SPA are served from
    /// DIFFERENT origins (confirmed by this same gate's own audit — separate
    /// CloudFront/S3 static hosting vs. the ALB-fronted Api), so a relative
    /// redirect would resolve against the Api's own origin and 404. Never a
    /// secret — just the frontend's own public base URL plus its integration
    /// route.
    /// </summary>
    public string? WebFrontendReturnUrl { get; set; }

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
