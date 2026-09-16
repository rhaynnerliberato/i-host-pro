using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>Returns one queued outcome per call, in order — lets a test script a multi-page sync (e.g. success-with-nextLink, then success-with-deltaLink, or success, then failure).</summary>
internal sealed class FakeAirbnbEmailMessageSource : IAirbnbEmailMessageSource
{
    private readonly Queue<AirbnbEmailDeltaFetchOutcome> _outcomes;

    public FakeAirbnbEmailMessageSource(params AirbnbEmailDeltaFetchOutcome[] outcomes) => _outcomes = new Queue<AirbnbEmailDeltaFetchOutcome>(outcomes);

    public List<(string AccessToken, string MailFolderId, string? Cursor)> Calls { get; } = [];

    public Task<AirbnbEmailDeltaFetchOutcome> GetDeltaPageAsync(
        string accessToken, string mailFolderId, string? deltaOrNextLink, CancellationToken cancellationToken)
    {
        Calls.Add((accessToken, mailFolderId, deltaOrNextLink));
        return Task.FromResult(_outcomes.Count > 0
            ? _outcomes.Dequeue()
            : AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([], null, "https://graph.microsoft.com/v1.0/unused-delta")));
    }

    public Dictionary<string, string?> BodiesByMessageId { get; } = [];
    public List<string> BodyFetchCalls { get; } = [];

    public Task<string?> GetMessageBodyAsync(string accessToken, string messageId, CancellationToken cancellationToken)
    {
        BodyFetchCalls.Add(messageId);
        return Task.FromResult(BodiesByMessageId.GetValueOrDefault(messageId));
    }
}
