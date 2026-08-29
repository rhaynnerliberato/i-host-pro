using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Application;

public interface IConversationRepository : IRepository<Conversation, Guid>
{
    /// <summary>The single lookup <see cref="ConversationResolver"/> needs (mandate item 19 — one active Conversation per Reservation+Channel, backstopped by a DB unique index, see <c>ConversationConfiguration</c>).</summary>
    Task<Conversation?> GetActiveByReservationAndChannelAsync(
        Guid reservationId, string channel, CancellationToken cancellationToken);
}
