using IHostPro.BuildingBlocks.Domain;
using Mediator;

namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// A write operation (CQRS). Dispatched through the "Mediator" library
/// (Martin Othamar, source-generator based — see ADR-002) rather than the
/// classic MediatR package.
/// </summary>
public interface ICommand : IRequest<Result>
{
}

public interface ICommand<TResponse> : IRequest<Result<TResponse>>
{
}

public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand, Result>
    where TCommand : ICommand
{
}

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, Result<TResponse>>
    where TCommand : ICommand<TResponse>
{
}
