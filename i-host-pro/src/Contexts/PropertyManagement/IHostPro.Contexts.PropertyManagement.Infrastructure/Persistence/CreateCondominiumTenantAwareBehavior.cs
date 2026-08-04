using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="CreateCondominiumCommand"/>
/// (Checkpoint 2 plan). Registered as a CLOSED generic
/// (<c>IPipelineBehavior&lt;CreateCondominiumCommand, Result&lt;CondominiumResult&gt;&gt;</c>),
/// never an open generic — replaces the shared, generic
/// <c>TenantTransactionBehavior&lt;,&gt;</c> for CreateCondominium
/// specifically, so its outbox publication is on the real production
/// dispatch path. No command-specific executor: unlike Update, Create has no
/// exception to translate (no unique constraint on a condominium's name),
/// so it delegates straight to <see cref="IPropertyManagementTransactionExecutor"/>.
/// </summary>
public sealed class CreateCondominiumTenantAwareBehavior : IPipelineBehavior<CreateCondominiumCommand, Result<CondominiumResult>>
{
    private readonly IPropertyManagementTransactionExecutor _transactionExecutor;

    public CreateCondominiumTenantAwareBehavior(IPropertyManagementTransactionExecutor transactionExecutor) =>
        _transactionExecutor = transactionExecutor;

    public async ValueTask<Result<CondominiumResult>> Handle(
        CreateCondominiumCommand message,
        MessageHandlerDelegate<CreateCondominiumCommand, Result<CondominiumResult>> next,
        CancellationToken cancellationToken) =>
        await _transactionExecutor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
