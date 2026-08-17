using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>CleaningCreated</c> (Fase 7, Incremento 2 —
/// Dashboard &amp; Reporting Foundation, Checkpoint 1 — ADR-016). Depends
/// ONLY on <see cref="IDashboardMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — never on <c>DashboardDbContext</c>,
/// <c>IDashboardTransactionExecutor</c>, or any projection synchronizer
/// directly.
/// </summary>
[NonTransactional]
public static class CleaningCreatedHandler
{
    public static Task Handle(
        CleaningCreated message,
        MessageContext context,
        IDashboardMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
