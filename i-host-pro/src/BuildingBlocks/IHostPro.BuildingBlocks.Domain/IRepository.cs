namespace IHostPro.BuildingBlocks.Domain;

/// <summary>
/// Generic repository contract. Each Bounded Context defines its own repository
/// interfaces (in its Application/Domain layer) typically extending this contract;
/// the concrete implementation lives in that context's Infrastructure layer using
/// Entity Framework Core. This interface carries no persistence-technology knowledge.
/// </summary>
public interface IRepository<TAggregate, in TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    Task<TAggregate?> GetByIdAsync(TId id, CancellationToken cancellationToken = default);

    void Add(TAggregate aggregate);

    void Update(TAggregate aggregate);

    void Remove(TAggregate aggregate);
}
