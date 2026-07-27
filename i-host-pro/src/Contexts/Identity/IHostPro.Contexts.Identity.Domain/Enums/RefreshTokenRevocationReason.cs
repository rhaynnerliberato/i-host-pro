namespace IHostPro.Contexts.Identity.Domain.Enums;

public enum RefreshTokenRevocationReason
{
    Rotated = 1,
    LogoutRequested = 2,
    ReuseDetected = 3,
    AdminRevoked = 4,
    Expired = 5
}
