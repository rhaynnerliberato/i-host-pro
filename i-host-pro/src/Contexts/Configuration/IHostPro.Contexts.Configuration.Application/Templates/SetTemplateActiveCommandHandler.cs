using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;

namespace IHostPro.Contexts.Configuration.Application.Templates;

public sealed class SetTemplateActiveCommandHandler : ICommandHandler<SetTemplateActiveCommand, TemplateResult>
{
    private static readonly Error TemplateNotFoundError = new(TemplateErrorCodes.TemplateNotFound, TemplateErrorCodes.TemplateNotFound);

    private readonly ITemplateRepository _repository;
    private readonly TimeProvider _timeProvider;

    public SetTemplateActiveCommandHandler(ITemplateRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<TemplateResult>> Handle(SetTemplateActiveCommand command, CancellationToken cancellationToken)
    {
        var template = await _repository.GetByKeyAsync(command.Key, cancellationToken);
        if (template is null)
            return Result.Failure<TemplateResult>(TemplateNotFoundError);

        var now = _timeProvider.GetUtcNow();
        if (command.IsActive)
            template.Activate(now);
        else
            template.Deactivate(now);

        _repository.Update(template);

        return Result.Success(CreateTemplateCommandHandler.ToResult(template));
    }
}
