using IHostPro.BuildingBlocks.Domain;
using Mediator;

namespace IHostPro.BuildingBlocks.Application;

/// <summary>A read operation (CQRS). Never mutates state.</summary>
public interface IQuery<TResponse> : IRequest<Result<TResponse>>
{
}

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, Result<TResponse>>
    where TQuery : IQuery<TResponse>
{
}
