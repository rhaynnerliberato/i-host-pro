using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="SetPropertyAccessConfigurationCommand"/>
/// (Fase 10, Checkpoint 6.2) — mirrors
/// <c>SetFrontDeskContactTenantAwareBehavior</c> exactly: no command-specific
/// exception to translate (the upsert-by-PropertyId lookup already prevents
/// a duplicate insert), so it delegates straight to
/// <see cref="IPropertyManagementTransactionExecutor"/>. No Integration
/// Event is published by this command, so there is nothing extra for this
/// behavior to flush beyond the transaction itself.
/// </summary>
public sealed class SetPropertyAccessConfigurationTenantAwareBehavior
    : IPipelineBehavior<SetPropertyAccessConfigurationCommand, Result<PropertyAccessConfigurationResult>>
{
    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;

    public SetPropertyAccessConfigurationTenantAwareBehavior(IPropertyManagementTransactionExecutor transactionExecutor) =>
        _transactionExecutor = transactionExecutor;

    public async ValueTask<Result<PropertyAccessConfigurationResult>> Handle(
        SetPropertyAccessConfigurationCommand message,
        MessageHandlerDelegate<SetPropertyAccessConfigurationCommand, Result<PropertyAccessConfigurationResult>> next,
        CancellationToken cancellationToken) =>
        await _transactionExecutor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
