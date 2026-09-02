using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.Communication.Contracts;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Application;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting), Decision Gate §9 —
/// proves <see cref="AgentContextBuilder"/>'s conversation-history token
/// budget: closes <c>ProductionContextBudgetStrategyRequired</c>
/// (<c>UnlimitedConversationContext=false</c>).
///
/// Pending action / handoff-session state are deliberately NOT covered here:
/// <see cref="AgentContextBuilder.BuildAsync"/> never assembles either into
/// its output (confirmed by direct code read — that state is handled
/// entirely downstream, in <c>ConversationMessageReceivedProcessor</c>,
/// never serialized into <see cref="ModelRequest.Messages"/>) — there is
/// nothing here for the budget to ever truncate, by construction, so no test
/// scenario for them applies to this class.
/// </summary>
public class AgentContextBuilderTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private static List<ConversationHistoryMessage> BuildHistory(int count, int contentLength = 20)
    {
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-count);
        return Enumerable.Range(0, count)
            .Select(i => new ConversationHistoryMessage(
                Guid.NewGuid(),
                i % 2 == 0 ? ConversationMessageDirection.Inbound : ConversationMessageDirection.Outbound,
                new string((char)('a' + i % 26), contentLength) + $"-msg{i}",
                baseTime.AddSeconds(i)))
            .ToList();
    }

    private static AgentContextBuilder BuildSut(IReadOnlyList<ConversationHistoryMessage> history, ContextBudgetOptions? budgetOptions = null) =>
        new(
            new FakeConversationHistoryReader(history),
            new FakeAiAgentBehaviorPolicyReader(),
            new FakePropertyLocalTimeContextReader(),
            new FakeContextBudgetPolicy(budgetOptions ?? new ContextBudgetOptions()),
            TimeProvider.System);

    [Fact]
    public async Task A_small_conversation_under_the_budget_is_never_truncated()
    {
        var history = BuildHistory(5);
        var sut = BuildSut(history, new ContextBudgetOptions { MaxHistoryTokens = 8000 });

        var request = await sut.BuildAsync(TenantId, ConversationId, history[^1].MessageId, ReservationId, CancellationToken.None);

        request.Messages.Should().HaveCount(5, "every message fits comfortably within a generous budget");
    }

    [Fact]
    public async Task A_large_conversation_over_the_budget_drops_the_oldest_messages_first()
    {
        // 50 messages, ~30 chars each (~9 estimated tokens each at the
        // default 3.5 chars/token) — a tight budget only fits the last few.
        var history = BuildHistory(50, contentLength: 30);
        var tightBudget = new ContextBudgetOptions { MaxHistoryTokens = 50 };
        var sut = BuildSut(history, tightBudget);

        var request = await sut.BuildAsync(TenantId, ConversationId, history[^1].MessageId, ReservationId, CancellationToken.None);

        request.Messages.Should().HaveCountLessThan(50, "UnlimitedConversationContext=false — a tight budget must actually truncate");
        request.Messages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_most_recent_messages_are_always_the_ones_preserved()
    {
        var history = BuildHistory(50, contentLength: 30);
        var tightBudget = new ContextBudgetOptions { MaxHistoryTokens = 50 };
        var sut = BuildSut(history, tightBudget);

        var request = await sut.BuildAsync(TenantId, ConversationId, history[^1].MessageId, ReservationId, CancellationToken.None);

        var keptCount = request.Messages.Count;
        var expectedSurvivingContents = history.Skip(50 - keptCount).Select(m => m.Content).ToArray();
        var actualContents = request.Messages.Select(m => m.Content).ToArray();

        actualContents.Should().Equal(expectedSurvivingContents, "the oldest messages must be dropped first, never the most recent ones");
    }

    [Fact]
    public async Task The_single_most_recent_message_is_kept_even_if_it_alone_exceeds_the_budget()
    {
        var history = BuildHistory(3, contentLength: 500);
        var tinyBudget = new ContextBudgetOptions { MaxHistoryTokens = 1 };
        var sut = BuildSut(history, tinyBudget);

        var request = await sut.BuildAsync(TenantId, ConversationId, history[^1].MessageId, ReservationId, CancellationToken.None);

        request.Messages.Should().HaveCount(1, "the model must always see at least the message it is replying to");
        request.Messages[0].Content.Should().Be(history[^1].Content);
    }

    [Fact]
    public async Task System_prompt_and_structured_context_are_never_truncated_regardless_of_history_size()
    {
        var history = BuildHistory(200, contentLength: 100);
        var tinyBudget = new ContextBudgetOptions { MaxHistoryTokens = 1 };
        var sut = BuildSut(history, tinyBudget);

        var request = await sut.BuildAsync(TenantId, ConversationId, history[^1].MessageId, ReservationId, CancellationToken.None);

        // The system prompt is assembled entirely separately from the
        // history budget (ComposeSystemPrompt) — its content/length never
        // depends on how much history survived truncation.
        request.SystemPrompt.Should().Contain("Data/hora atual (UTC)", "the current-time structured fact must survive even a 1-token history budget");
        request.SystemPrompt.Should().Contain("Você é um assistente operacional do iHostPro", "the safety-only fallback instructions must always be present");
    }

    [Fact]
    public async Task Disabling_the_budget_returns_the_full_history_unchanged()
    {
        var history = BuildHistory(200, contentLength: 100);
        var disabledBudget = new ContextBudgetOptions { Enabled = false, MaxHistoryTokens = 1 };
        var sut = BuildSut(history, disabledBudget);

        var request = await sut.BuildAsync(TenantId, ConversationId, history[^1].MessageId, ReservationId, CancellationToken.None);

        request.Messages.Should().HaveCount(200, "Enabled=false must be a genuine escape hatch, never a partial one");
    }

    [Fact]
    public async Task Surviving_messages_are_always_returned_in_chronological_order_with_the_triggering_message_last()
    {
        var history = BuildHistory(20, contentLength: 30);
        var tightBudget = new ContextBudgetOptions { MaxHistoryTokens = 40 };
        var sut = BuildSut(history, tightBudget);

        var request = await sut.BuildAsync(TenantId, ConversationId, history[^1].MessageId, ReservationId, CancellationToken.None);

        var expectedOrder = history.Select(m => m.Content).ToArray();
        var actualOrderWithinSurvivors = request.Messages.Select(m => m.Content).ToArray();

        // Every surviving message must still appear in the same relative
        // (chronological) order it had in the original history.
        var indices = actualOrderWithinSurvivors.Select(c => Array.IndexOf(expectedOrder, c)).ToArray();
        indices.Should().BeInAscendingOrder("truncation must never reorder the surviving messages");
        request.Messages[^1].Content.Should().Be(history[^1].Content, "the triggering message must always be last");
    }

    private sealed class FakeConversationHistoryReader : IConversationHistoryReader
    {
        private readonly IReadOnlyList<ConversationHistoryMessage> _history;
        public FakeConversationHistoryReader(IReadOnlyList<ConversationHistoryMessage> history) => _history = history;
        public Task<IReadOnlyList<ConversationHistoryMessage>> GetHistoryAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(_history);
    }

    private sealed class FakeAiAgentBehaviorPolicyReader : IAiAgentBehaviorPolicyReader
    {
        public Task<PolicyReadResult<AiAgentBehaviorPolicy>> GetEffectiveAsync(Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyReadResult<AiAgentBehaviorPolicy>.NotConfigured());
    }

    private sealed class FakePropertyLocalTimeContextReader : IPropertyLocalTimeContextReader
    {
        public Task<PropertyLocalTimeContext?> GetByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken) =>
            Task.FromResult<PropertyLocalTimeContext?>(null);
    }

    private sealed class FakeContextBudgetPolicy : IContextBudgetPolicy
    {
        public FakeContextBudgetPolicy(ContextBudgetOptions options) => Current = options;
        public ContextBudgetOptions Current { get; }
    }
}
