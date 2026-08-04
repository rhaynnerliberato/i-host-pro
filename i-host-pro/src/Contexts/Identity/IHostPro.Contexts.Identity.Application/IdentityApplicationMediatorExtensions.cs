using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Registers Mediator's generated dispatch (<c>IMediator</c>/<c>ISender</c>)
/// and every <c>ICommandHandler</c>/<c>IRequestHandler</c> implementation in
/// this assembly (<see cref="LoginCommandHandler"/>,
/// <see cref="RefreshTokenCommandHandler"/>, <see cref="LogoutCommandHandler"/>)
/// — Incremento 2 plan, Etapa 14.
///
/// The literal call to the source-generated <c>AddMediator()</c> must live
/// HERE, inside this project's own compilation: <c>Mediator.SourceGenerator</c>
/// (referenced only by this project, where the handlers are defined)
/// discovers what to register by analyzing the compilation containing the
/// call site, not via runtime reflection. <c>IHostPro.Api</c>'s composition
/// root calls this wrapper instead of calling the generated method directly.
///
/// <c>ServiceLifetime.Scoped</c> is required, not cosmetic: Mediator's default
/// (Singleton) makes the generated <c>RequestHandlerWrapper&lt;,&gt;</c> resolve
/// the concrete handler and every pipeline behavior exactly once, from the
/// root provider, and cache the resulting delegate chain for the process
/// lifetime — silently turning any Scoped dependency reached from a handler
/// (here, <c>IdentityDbContext</c> via the tenant bootstrap reader and the
/// refresh/logout executors) into a de-facto singleton shared by every
/// concurrent request. That is what produced the EF Core
/// <c>ConcurrencyDetector</c> failure under two concurrent HTTP refresh
/// requests. Scoped makes Mediator re-resolve the handler and behaviors from
/// each request's own DI scope, matching <c>IdentityDbContext</c>'s own
/// (default) Scoped lifetime.
/// </summary>
public static class IdentityApplicationMediatorExtensions
{
    /// <summary>
    /// Registers this project's own generated <c>Mediator.Mediator</c> AND
    /// <see cref="IIdentityRequestDispatcher"/> (Checkpoint 6 homologação —
    /// cross-context <c>ISender</c> ambiguity fix; see
    /// <see cref="IIdentityRequestDispatcher"/>'s own doc comment). The
    /// dispatcher registration must come after <c>AddMediator()</c> so its
    /// constructor can resolve the concrete <c>Mediator.Mediator</c> type
    /// <c>AddMediator()</c> just registered.
    /// </summary>
    public static IServiceCollection AddIdentityApplicationMediator(this IServiceCollection services)
    {
        services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
        services.AddScoped<IIdentityRequestDispatcher, IdentityRequestDispatcher>();
        return services;
    }
}
