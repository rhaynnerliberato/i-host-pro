using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Trims a Mediator-generated handler registration down to an explicit
/// allowlist (Fase 11, Checkpoint 3 — Option A query wiring: read Queries
/// promoted to a Bounded Context's shared Module, consumed in-process by
/// trusted internal consumers like the AI Agent's own Worker-hosted Read
/// Tools, while write Commands stay Api-only).
///
/// <c>Mediator.SourceGenerator</c>'s own <c>AddMediator()</c> is all-or-nothing
/// per assembly: it unconditionally registers EVERY discovered
/// <c>Mediator.IRequestHandler&lt;,&gt;</c> implementation in the compilation
/// as its own scoped DI service — including write Command handlers whose
/// dependencies (readers/repositories/executors) are deliberately never
/// registered in a Worker-shared Module. Left unfiltered, those write
/// handlers' own unresolvable constructor dependencies fail
/// <c>Host.CreateApplicationBuilder</c>'s default <c>ValidateOnBuild=true</c>
/// at startup — discovered as a real Worker crash (Fase 11, Checkpoint 3
/// homologation) — even though nothing in the Worker process ever invokes
/// them.
///
/// Call <see cref="KeepOnlyMediatorHandlers"/> immediately after
/// <c>AddXApplicationMediator()</c> to remove every OTHER handler's
/// registration FROM THAT SAME TARGET ASSEMBLY. This is safe and precisely
/// targeted: <c>Mediator.Mediator</c>'s own generated constructor only
/// resolves <c>IServiceProvider</c> at validation time (<c>ValidateOnBuild</c>
/// walks constructor PARAMETER TYPES statically — it never executes a
/// constructor body, so <c>Mediator.Mediator</c>'s own internal
/// <c>GetRequiredService(...)</c> calls are never reached during
/// validation); and each handler's generated <c>RequestHandlerWrapper&lt;,&gt;</c>
/// stays registered and untouched — its own constructor is parameterless, it
/// resolves the concrete handler LAZILY inside <c>Handle()</c>, only when a
/// request of that exact type is actually dispatched. Removing only the
/// disallowed handler registrations is therefore both necessary and
/// sufficient — no write dependency is ever registered, no
/// <c>ValidateOnBuild</c> weakening, no secondary <c>IServiceProvider</c>, no
/// reflection hack beyond a standard "does this type implement
/// <c>IRequestHandler&lt;,&gt;</c>" interface check.
///
/// Removal candidates are scoped to <paramref name="allowedHandlerTypes"/>'s
/// own declaring assembly — never the whole <see cref="IServiceCollection"/>.
/// This method is called once PER Bounded Context (Worker's own Program.cs
/// calls it five times, once right after each <c>AddXModule</c>); an
/// unscoped "remove anything not in THIS call's allowlist" filter would
/// silently strip every EARLIER call's own already-approved handlers too,
/// since each call only knows about its own two or three types — a real bug
/// found and fixed during CP3 homologation (only the LAST-called context's
/// handler ever survived all five calls).
/// </summary>
public static class MediatorHandlerAllowlistExtensions
{
    public static IServiceCollection KeepOnlyMediatorHandlers(this IServiceCollection services, params Type[] allowedHandlerTypes)
    {
        if (allowedHandlerTypes.Length == 0)
            throw new ArgumentException("At least one allowed handler type must be supplied.", nameof(allowedHandlerTypes));

        var targetAssembly = allowedHandlerTypes[0].Assembly;
        if (allowedHandlerTypes.Any(t => t.Assembly != targetAssembly))
        {
            throw new ArgumentException(
                "All allowed handler types must belong to the same assembly — call this once per target Bounded Context.",
                nameof(allowedHandlerTypes));
        }

        var toRemove = services
            .Where(descriptor =>
                descriptor.ImplementationType is { IsClass: true, IsAbstract: false } implementationType
                && implementationType.Assembly == targetAssembly
                && ImplementsMediatorRequestHandler(implementationType)
                && !allowedHandlerTypes.Contains(implementationType))
            .ToList();

        foreach (var descriptor in toRemove)
            services.Remove(descriptor);

        return services;
    }

    // Matched by interface full-name rather than a compile-time reference to
    // the Mediator package — this project has no reason to take on that
    // dependency just for a single interface-shape check.
    private static bool ImplementsMediatorRequestHandler(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition().FullName == "Mediator.IRequestHandler`2");
}
