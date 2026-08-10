using IHostPro.BuildingBlocks.Messaging.Abstractions;
using Wolverine.Runtime;

namespace IHostPro.BuildingBlocks.Infrastructure.Multitenancy;

/// <summary>
/// Wolverine middleware that resolves the scoped <see cref="ITenantContext"/>
/// for IHostPro.Worker from the TenantId carried by every Integration Event
/// envelope, before the actual handler runs — mirroring, for consumed
/// messages, the same responsibility an authentication middleware has for
/// HTTP requests in IHostPro.Api (Architecture Principles, Section 7).
/// Registered globally, filtered to message types assignable to
/// <see cref="IntegrationEvent"/> (see WolverineOptions registration in the
/// Host processes) — this class itself must stay agnostic of any single
/// Bounded Context's concrete event type (Architecture Principles §11
/// allows BuildingBlocks.Infrastructure to reference Wolverine types
/// directly, but never a specific context's Contracts assembly).
///
/// Checkpoint 7 homologação (Fase 5), real defect found and fixed, in two
/// stages, the first time this middleware was ever exercised end-to-end
/// against a real running Worker consuming a real message: (1) a message
/// parameter typed as the abstract base <see cref="IntegrationEvent"/>
/// itself failed with <c>JasperFx.CodeGeneration.UnResolvableVariableException</c>
/// — Wolverine's code generator tracks the message variable for a chain as
/// its own concrete declared type (e.g. <c>PolicyUpdated</c>), never
/// upcasting it to a requested base-type parameter; (2) an open generic
/// <c>Before&lt;TMessage&gt;</c> failed the same way, with the literal
/// unbound type parameter name in the exception — the raw
/// <c>opts.Policies.AddMiddleware(Type, Func&lt;HandlerChain,bool&gt;)</c>
/// policy used to register this class does not close generic methods over
/// each chain's message type. The fix sidesteps message-type-based
/// parameter resolution entirely: <see cref="MessageContext"/> is always
/// resolvable in a generated Wolverine method regardless of message type
/// (confirmed directly in both failing stack traces above), so this reads
/// the message from <c>context.Envelope.Message</c> and casts it, rather
/// than asking Wolverine to inject it by declared parameter type.
/// </summary>
public static class TenantResolutionMiddleware
{
    public static void Before(MessageContext context, ITenantContext tenantContext)
    {
        var message = (IntegrationEvent)context.Envelope!.Message!;
        tenantContext.SetTenant(message.TenantId);
    }
}
