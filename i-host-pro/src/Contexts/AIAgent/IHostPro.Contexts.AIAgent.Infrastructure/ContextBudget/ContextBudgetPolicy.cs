using IHostPro.Contexts.AIAgent.Application;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.AIAgent.Infrastructure.ContextBudget;

/// <inheritdoc cref="IContextBudgetPolicy"/>
public sealed class ContextBudgetPolicy : IContextBudgetPolicy
{
    private readonly IOptions<ContextBudgetOptions> _options;

    public ContextBudgetPolicy(IOptions<ContextBudgetOptions> options) => _options = options;

    public ContextBudgetOptions Current => _options.Value;
}
