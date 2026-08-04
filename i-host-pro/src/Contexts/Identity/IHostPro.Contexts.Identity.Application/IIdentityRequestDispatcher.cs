using Mediator;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// This Bounded Context's own request dispatcher (Checkpoint 6 homologação —
/// Mediator <c>ISender</c> cross-context ambiguity fix). <c>Mediator.SourceGenerator</c>
/// generates a distinct, assembly-scoped concrete <c>Mediator.Mediator</c>
/// type per project that calls <c>AddMediator()</c> — <c>Identity.Application</c>'s
/// own <c>Mediator.Mediator</c> and <c>PropertyManagement.Application</c>'s own
/// <c>Mediator.Mediator</c> are two genuinely different CLR types (same name,
/// different assemblies), each knowing only about handlers declared in its
/// own compilation. When both projects register themselves in the same
/// container (only <c>IHostPro.Api</c> does — no test file ever composed both
/// together, which is why this went undetected), the shared
/// <c>Mediator.ISender</c>/<c>IMediator</c>/<c>IPublisher</c> interfaces
/// resolve to whichever module's registration won the DI container's
/// first-registration race — the OTHER module's commands then fail with
/// <c>MissingMessageHandlerException</c>, indistinguishable from a genuine
/// missing handler.
///
/// This interface — one per Bounded Context — sidesteps the ambiguity
/// entirely: <see cref="IdentityRequestDispatcher"/> depends on THIS
/// project's own concrete <c>Mediator.Mediator</c> type directly (never the
/// shared interfaces), so DI resolves it unambiguously regardless of what
/// else is registered in the same container. Exposes only the single
/// overload every real consumer in this codebase actually calls — no
/// <c>Publish</c>/<c>CreateStream</c>/non-generic <c>Send(object, ...)</c> is
/// used anywhere (confirmed by inspection before adding this interface).
/// </summary>
public interface IIdentityRequestDispatcher
{
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
