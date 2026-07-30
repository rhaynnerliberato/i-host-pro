namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// One of the authenticated caller's own sessions (Incremento 3, Checkpoint
/// 4). Deliberately excludes <c>IpAddress</c> (never exposed by default) and
/// <c>ApproximateLocation</c> (not part of this checkpoint's approved field
/// list). <c>Device</c> is deliberately NOT exposed either — checked at the
/// start of Checkpoint 5: no code path in this codebase ever populates
/// <see cref="Domain.Session.Device"/> with a non-null value (<c>AuthController.CaptureRequestContext</c>
/// hardcodes it to <c>null</c>), so surfacing it would expose a field that
/// can only ever read as an always-null artifact, not real device data —
/// re-add it only once a real capture mechanism exists. <see cref="Browser"/>
/// IS genuinely persisted (the raw User-Agent header, captured at
/// login/refresh time) and is passed through exactly as-is — never derived,
/// parsed or inferred here (Incremento 3, Checkpoint 4, explicit
/// requirement: "não inventar processamento de dispositivo ou navegador").
/// </summary>
public sealed record OwnSessionResult(
    Guid SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    bool IsCurrent,
    string? Browser);
