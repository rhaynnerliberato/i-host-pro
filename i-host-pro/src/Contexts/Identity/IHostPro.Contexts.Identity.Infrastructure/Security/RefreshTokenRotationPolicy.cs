using IHostPro.Contexts.Identity.Application;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <inheritdoc cref="IRefreshTokenRotationPolicy"/>
public sealed class RefreshTokenRotationPolicy : IRefreshTokenRotationPolicy
{
    private readonly IOptions<RefreshTokenOptions> _options;

    public RefreshTokenRotationPolicy(IOptions<RefreshTokenOptions> options) => _options = options;

    public TimeSpan ConcurrentRotationGraceWindow => _options.Value.ConcurrentRotationGraceWindow;
}
