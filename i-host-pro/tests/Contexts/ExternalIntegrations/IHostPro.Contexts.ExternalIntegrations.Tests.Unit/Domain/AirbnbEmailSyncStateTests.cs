using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class AirbnbEmailSyncStateTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid MailboxConnectionId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_with_no_cursor_and_no_history()
    {
        var state = AirbnbEmailSyncState.Create(Guid.NewGuid(), TenantId, MailboxConnectionId, "inbox", Now);

        state.MailFolderId.Should().Be("inbox");
        state.DeltaLink.Should().BeNull();
        state.LastSuccessfulSyncAtUtc.Should().BeNull();
        state.LastErrorCode.Should().BeNull();
    }

    [Fact]
    public void Create_rejects_an_empty_mail_folder_id()
    {
        var act = () => AirbnbEmailSyncState.Create(Guid.NewGuid(), TenantId, MailboxConnectionId, " ", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecordSuccess_advances_the_cursor_and_clears_any_prior_error()
    {
        var state = AirbnbEmailSyncState.Create(Guid.NewGuid(), TenantId, MailboxConnectionId, "inbox", Now);
        state.RecordFailure("GRAPH_THROTTLED", Now.AddMinutes(1));

        state.RecordSuccess("delta-link-1", Now.AddMinutes(2));

        state.DeltaLink.Should().Be("delta-link-1");
        state.LastSuccessfulSyncAtUtc.Should().Be(Now.AddMinutes(2));
        state.LastErrorCode.Should().BeNull();
    }

    [Fact]
    public void RecordFailure_never_advances_the_cursor()
    {
        var state = AirbnbEmailSyncState.Create(Guid.NewGuid(), TenantId, MailboxConnectionId, "inbox", Now);
        state.RecordSuccess("delta-link-1", Now.AddMinutes(1));

        state.RecordFailure("GRAPH_UNAVAILABLE", Now.AddMinutes(2));

        state.DeltaLink.Should().Be("delta-link-1", "a failed batch must never move the cursor past the last durable success");
        state.LastSuccessfulSyncAtUtc.Should().Be(Now.AddMinutes(1));
        state.LastErrorCode.Should().Be("GRAPH_UNAVAILABLE");
        state.LastAttemptAtUtc.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void Reset_discards_the_cursor_entirely()
    {
        var state = AirbnbEmailSyncState.Create(Guid.NewGuid(), TenantId, MailboxConnectionId, "inbox", Now);
        state.RecordSuccess("delta-link-1", Now.AddMinutes(1));

        state.Reset(Now.AddMinutes(2));

        state.DeltaLink.Should().BeNull();
        state.LastSuccessfulSyncAtUtc.Should().BeNull();
        state.LastErrorCode.Should().BeNull();
    }
}
