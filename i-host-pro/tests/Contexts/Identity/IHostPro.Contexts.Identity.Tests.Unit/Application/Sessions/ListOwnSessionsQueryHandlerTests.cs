using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Sessions;

public class ListOwnSessionsQueryHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    private static Session NewSession(DateTimeOffset lastActivityAt, Guid? id = null) =>
        Session.Open(id ?? Guid.NewGuid(), TenantId, UserId, lastActivityAt, "iPhone", "Safari", "203.0.113.7");

    [Fact]
    public async Task Calls_the_reader_with_the_querys_user_id()
    {
        var reader = FakeSessionReader.WithSessions();
        var handler = new ListOwnSessionsQueryHandler(reader);

        await handler.Handle(new ListOwnSessionsQuery(UserId, Guid.NewGuid()), CancellationToken.None);

        reader.CallCount.Should().Be(1);
        reader.LastUserId.Should().Be(UserId);
    }

    [Fact]
    public async Task An_empty_collection_from_the_reader_is_a_valid_success_result()
    {
        var reader = FakeSessionReader.WithSessions();
        var handler = new ListOwnSessionsQueryHandler(reader);

        var result = await handler.Handle(new ListOwnSessionsQuery(UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Sessions_are_ordered_by_most_recently_active_first_then_by_id()
    {
        var now = DateTimeOffset.UtcNow;
        var olderId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var newerButSameTimestampId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var older = NewSession(now.AddMinutes(-10));
        var mostRecent = NewSession(now);
        var tiedA = NewSession(now.AddMinutes(-1), olderId);
        var tiedB = NewSession(now.AddMinutes(-1), newerButSameTimestampId);
        var reader = FakeSessionReader.WithSessions(older, tiedB, mostRecent, tiedA);
        var handler = new ListOwnSessionsQueryHandler(reader);

        var result = await handler.Handle(new ListOwnSessionsQuery(UserId, Guid.NewGuid()), CancellationToken.None);

        result.Value.Select(s => s.SessionId).Should().Equal(mostRecent.Id, tiedA.Id, tiedB.Id, older.Id);
    }

    [Fact]
    public async Task Exactly_one_session_is_marked_current_when_its_id_matches_the_claim()
    {
        var current = NewSession(DateTimeOffset.UtcNow);
        var other = NewSession(DateTimeOffset.UtcNow.AddMinutes(-1));
        var reader = FakeSessionReader.WithSessions(current, other);
        var handler = new ListOwnSessionsQueryHandler(reader);

        var result = await handler.Handle(new ListOwnSessionsQuery(UserId, current.Id), CancellationToken.None);

        result.Value.Should().ContainSingle(s => s.IsCurrent).Which.SessionId.Should().Be(current.Id);
    }

    [Fact]
    public async Task No_session_is_marked_current_when_the_claim_matches_none_of_them()
    {
        var sessionA = NewSession(DateTimeOffset.UtcNow);
        var sessionB = NewSession(DateTimeOffset.UtcNow.AddMinutes(-1));
        var reader = FakeSessionReader.WithSessions(sessionA, sessionB);
        var handler = new ListOwnSessionsQueryHandler(reader);

        var result = await handler.Handle(new ListOwnSessionsQuery(UserId, Guid.NewGuid()), CancellationToken.None);

        result.Value.Should().OnlyContain(s => !s.IsCurrent);
    }

    [Fact]
    public async Task Cancellation_token_is_propagated_to_the_reader()
    {
        var reader = FakeSessionReader.WithSessions();
        var handler = new ListOwnSessionsQueryHandler(reader);
        using var cts = new CancellationTokenSource();

        await handler.Handle(new ListOwnSessionsQuery(UserId, Guid.NewGuid()), cts.Token);

        reader.LastCancellationToken.Should().Be(cts.Token);
    }
}
