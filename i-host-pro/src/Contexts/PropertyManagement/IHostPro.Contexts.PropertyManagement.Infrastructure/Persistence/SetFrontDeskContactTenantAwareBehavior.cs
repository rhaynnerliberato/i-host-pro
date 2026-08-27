using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.FrontDesk;
using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="SetFrontDeskContactCommand"/>
/// (Fase 10, Checkpoint 4) — mirrors <c>CreateCondominiumTenantAwareBehavior</c>
/// exactly: no command-specific exception to translate (no unique
/// constraint the handler needs to catch — the upsert-by-CondominiumId
/// lookup already prevents a duplicate insert), so it delegates straight to
/// <see cref="IPropertyManagementTransactionExecutor"/>. No Integration
/// Event is published by this command, so there is nothing extra for this
/// behavior to flush beyond the transaction itself.
/// </summary>
public sealed class SetFrontDeskContactTenantAwareBehavior : IPipelineBehavior<SetFrontDeskContactCommand, Result<FrontDeskContactResult>>
{
    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;

    public SetFrontDeskContactTenantAwareBehavior(IPropertyManagementTransactionExecutor transactionExecutor) =>
        _transactionExecutor = transactionExecutor;

    public async ValueTask<Result<FrontDeskContactResult>> Handle(
        SetFrontDeskContactCommand message,
        MessageHandlerDelegate<SetFrontDeskContactCommand, Result<FrontDeskContactResult>> next,
        CancellationToken cancellationToken) =>
        await _transactionExecutor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
